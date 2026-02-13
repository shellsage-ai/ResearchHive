using ResearchHive.Core.Models;
using System.Text;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Fuses multiple RepoProfiles (or prior ProjectFusionArtifacts) into a unified
/// architecture / merge / extension / comparison document with full provenance.
/// Uses grounded prompts with anti-hallucination rules to ensure accuracy.
/// </summary>
public class ProjectFusionEngine
{
    private readonly SessionManager _sessionManager;
    private readonly LlmService _llmService;

    public ProjectFusionEngine(SessionManager sessionManager, LlmService llmService)
    {
        _sessionManager = sessionManager;
        _llmService = llmService;
    }

    // ─────────────── Section names ───────────────

    private static readonly string[] AllSections = new[]
    {
        "PROJECT_IDENTITIES", "UNIFIED_VISION", "ARCHITECTURE", "TECH_STACK",
        "FEATURE_MATRIX", "GAPS_CLOSED", "NEW_GAPS", "IP_NOTES", "PROVENANCE"
    };

    // ─────────────── Goal descriptions (user-facing, shown in report) ───────────────

    public static string GoalDescription(ProjectFusionGoal goal) => goal switch
    {
        ProjectFusionGoal.Merge =>
            "**Merge** — Combine both projects into a single unified project, resolving overlaps and leveraging the best of each.",
        ProjectFusionGoal.Extend =>
            "**Extend** — Use the first project as the base and integrate capabilities from the other(s) as extensions, plugins, or new modules.",
        ProjectFusionGoal.Compare =>
            "**Compare** — Side-by-side analysis of architectures, tech stacks, strengths, and weaknesses with actionable recommendations.",
        ProjectFusionGoal.Architect =>
            "**Architect** — Design a brand-new system that draws the best ideas from all inputs without directly merging their code.",
        _ => "Analyze and fuse these project inputs."
    };

    public async Task<ProjectFusionArtifact> RunAsync(string sessionId, ProjectFusionRequest request, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        // 1. Create tracking job
        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.ProjectFusion,
            Prompt = $"Project Fusion ({request.Goal}): {request.FocusPrompt}",
            State = JobState.Planning
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Project Fusion Started", $"Goal: {request.Goal} | Inputs: {request.Inputs.Count}");

        try
        {
            // 2. Gather input data
            job.State = JobState.Searching;
            db.SaveJob(job);

            var inputBlocks = new List<string>();
            var inputTitles = new List<string>();
            var inputUrls = new List<string>();

            foreach (var input in request.Inputs)
            {
                if (input.Type == FusionInputType.RepoProfile)
                {
                    var profiles = db.GetRepoProfiles();
                    var profile = profiles.FirstOrDefault(p => p.Id == input.Id);
                    if (profile != null)
                    {
                        inputBlocks.Add(FormatProfileForLlm(profile));
                        inputTitles.Add($"{profile.Owner}/{profile.Name}");
                        inputUrls.Add(profile.RepoUrl);
                    }
                }
                else if (input.Type == FusionInputType.FusionArtifact)
                {
                    var fusions = db.GetProjectFusions();
                    var fusion = fusions.FirstOrDefault(f => f.Id == input.Id);
                    if (fusion != null)
                    {
                        inputBlocks.Add(FormatFusionForLlm(fusion));
                        inputTitles.Add(fusion.Title);
                        inputUrls.Add("(prior fusion)");
                    }
                }
            }

            AddReplay(job, "gather", "Inputs Loaded", $"Loaded {inputBlocks.Count} inputs: {string.Join(", ", inputTitles)}");

            if (inputBlocks.Count == 0)
                throw new InvalidOperationException("No valid inputs found for fusion.");

            // 3. Build the fusion prompt
            job.State = JobState.Drafting;
            db.SaveJob(job);

            var goalInstruction = GetGoalInstruction(request.Goal);
            var systemPrompt = BuildSystemPrompt();

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("# Project Fusion Request");
            userPrompt.AppendLine();
            userPrompt.AppendLine($"**Goal:** {goalInstruction}");
            if (!string.IsNullOrWhiteSpace(request.FocusPrompt))
                userPrompt.AppendLine($"**Focus:** {request.FocusPrompt}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("---");
            userPrompt.AppendLine();

            for (int i = 0; i < inputBlocks.Count; i++)
            {
                userPrompt.AppendLine($"## Input {i + 1}: {inputTitles[i]}");
                userPrompt.AppendLine(inputBlocks[i]);
                userPrompt.AppendLine("---");
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine(BuildSectionInstructions(request.Goal));

            AddReplay(job, "fuse", "Generating Fusion Outline", $"Sending {inputBlocks.Count} inputs to LLM for section outline");

            // --- Outline-then-expand: Call 1 — get a structured outline, Call 2-9 — expand each section ---
            var outlinePrompt = new StringBuilder();
            outlinePrompt.AppendLine(userPrompt.ToString());
            outlinePrompt.AppendLine();
            outlinePrompt.AppendLine("IMPORTANT: For now, produce ONLY a bullet-point outline of each section (3-6 bullets per section).");
            outlinePrompt.AppendLine("Do NOT write full prose yet. Just key points, decisions, and items for each section.");
            outlinePrompt.AppendLine("Label each section clearly. Reference specific project names and technologies from the input data.");

            var outlineResponse = await _llmService.GenerateAsync(outlinePrompt.ToString(), systemPrompt, 2000, ct: ct);
            AddReplay(job, "outline", "Outline Generated", "Expanding individual sections...");

            // Build shared context block (compact version of inputs for section calls)
            var compactContext = new StringBuilder();
            for (int i = 0; i < inputBlocks.Count; i++)
                compactContext.AppendLine($"## Input {i + 1}: {inputTitles[i]}\n{inputBlocks[i]}");
            var contextStr = compactContext.ToString();

            // Expand sections in parallel batches of 4
            var sectionResults = new Dictionary<string, string>();
            for (int batch = 0; batch < AllSections.Length; batch += 4)
            {
                var tasks = new List<Task<(string name, string content)>>();
                for (int j = batch; j < Math.Min(batch + 4, AllSections.Length); j++)
                {
                    var sectionName = AllSections[j];
                    tasks.Add(ExpandSectionAsync(sectionName, request.Goal, outlineResponse, contextStr,
                        goalInstruction, systemPrompt, ct));
                }

                var results = await Task.WhenAll(tasks);
                foreach (var (name, content) in results)
                    sectionResults[name] = content;
            }

            AddReplay(job, "sections", "All Sections Expanded", $"Expanded {sectionResults.Count} sections");

            // 4. Build artifact from expanded sections
            job.State = JobState.Evaluating;
            db.SaveJob(job);

            // Combine all sections into a full text for parsing helpers
            var fullText = new StringBuilder();
            foreach (var section in AllSections)
            {
                fullText.AppendLine($"{section}:");
                fullText.AppendLine(sectionResults.GetValueOrDefault(section, ""));
                fullText.AppendLine();
            }
            var combinedResponse = fullText.ToString();

            var artifact = new ProjectFusionArtifact
            {
                SessionId = sessionId,
                JobId = job.Id,
                Title = $"Fusion: {string.Join(" + ", inputTitles)} ({request.Goal})",
                InputSummary = string.Join(", ", inputTitles),
                Inputs = request.Inputs,
                Goal = request.Goal,
                ProjectIdentities = ExtractSection(combinedResponse, "PROJECT_IDENTITIES"),
                UnifiedVision = ExtractSection(combinedResponse, "UNIFIED_VISION"),
                ArchitectureProposal = ExtractSection(combinedResponse, "ARCHITECTURE"),
                TechStackDecisions = ExtractSection(combinedResponse, "TECH_STACK"),
                FeatureMatrix = ParseFeatureMatrix(combinedResponse),
                GapsClosed = ParseList(ExtractSection(combinedResponse, "GAPS_CLOSED")),
                NewGaps = ParseList(ExtractSection(combinedResponse, "NEW_GAPS")),
                IpNotes = ParseIpNotes(ExtractSection(combinedResponse, "IP_NOTES")),
                ProvenanceMap = ParseProvenanceMap(combinedResponse)
            };

            AddReplay(job, "parsed", "Fusion Parsed",
                $"Features: {artifact.FeatureMatrix.Count} | Gaps closed: {artifact.GapsClosed.Count} | New gaps: {artifact.NewGaps.Count}");

            // 5. Persist
            db.SaveProjectFusion(artifact);

            // 6. Generate full report
            job.State = JobState.Reporting;
            db.SaveJob(job);

            var report = GenerateReport(artifact, inputTitles, inputUrls);
            var reportRecord = new Report
            {
                SessionId = sessionId,
                JobId = job.Id,
                ReportType = "ProjectFusion",
                Title = artifact.Title,
                Content = report,
                Format = "markdown",
                ModelUsed = _llmService.LastModelUsed
            };
            db.SaveReport(reportRecord);

            job.State = JobState.Completed;
            job.FullReport = report;
            job.ModelUsed = _llmService.LastModelUsed;
            job.ExecutiveSummary = $"Fused {inputTitles.Count} projects ({request.Goal}): {artifact.FeatureMatrix.Count} features mapped, {artifact.GapsClosed.Count} gaps closed.";
            db.SaveJob(job);
            AddReplay(job, "complete", "Fusion Complete", job.ExecutiveSummary);
            db.SaveJob(job);

            return artifact;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            db.SaveJob(job);
            throw;
        }
    }

    // ─────────────── System Prompt (anti-hallucination grounded) ───────────────

    private static string BuildSystemPrompt() => @"You are a senior software architect performing a project fusion analysis.

CRITICAL GROUNDING RULES — follow these exactly:
1. ONLY reference technologies, frameworks, libraries, and tools that are explicitly listed in the input project data (dependencies, frameworks, languages, strengths, or code architecture summary).
2. If a technology is NOT in the input's dependencies, frameworks, or strengths, do NOT mention or suggest it. Inventing technologies is a critical error.
3. Every architectural claim must be traceable to specific input data — do not invent capabilities that are not evidenced.
4. Always include the source project name (e.g., ""AutoMapper"" or ""ResearchHive"") when referencing where a feature, strength, or decision comes from.
5. Be specific and concrete — describe what THESE specific projects do and how they relate. No generic boilerplate.
6. Use formatting: **bold** for key terms, markdown tables where comparing items, bullet points for lists.
7. When listing technologies, ONLY list ones that appear in the Dependencies, Frameworks, or Languages fields of the input data.";

    // ─────────────── Goal Instructions (detailed per mode) ───────────────

    private static string GetGoalInstruction(ProjectFusionGoal goal) => goal switch
    {
        ProjectFusionGoal.Merge => @"MERGE these projects into one unified project.
- Describe what the combined project would BE — its identity, purpose, and value proposition.
- Show how their architectures would integrate (shared layers, adapters, unified data flow).
- Resolve overlapping features: which implementation wins and why.
- Identify concrete integration points and potential conflicts.
- Only reference technologies and packages that exist in the input data.",

        ProjectFusionGoal.Extend => @"EXTEND the first project using capabilities from the other(s).
- The first project is the BASE — describe it clearly. Others provide extensions.
- Show exactly which capabilities from each extension project would be added.
- Describe the integration approach: new modules, adapters, configuration changes.
- Identify what changes the base project needs to accommodate the extensions.
- Only reference technologies and packages that exist in the input data.",

        ProjectFusionGoal.Compare => @"COMPARE these projects side-by-side.
- Describe what each project IS and DOES (identity, purpose, capabilities).
- Use comparison tables for architecture, tech stack, features, and maturity.
- Highlight where one excels and the other has gaps.
- Provide actionable recommendations: which to choose for specific use cases.
- Only reference technologies and packages that exist in the input data.",

        ProjectFusionGoal.Architect => @"ARCHITECT a new system inspired by the best ideas from all inputs.
- You are NOT merging code — you are designing a new system.
- Identify the strongest patterns, architectures, and approaches from each input.
- Propose a new architecture that cherry-picks the best of each.
- Be explicit about which ideas come from which project.
- Only reference technologies and packages that exist in the input data.",

        _ => "Analyze and fuse these project inputs."
    };

    // ─────────────── Section Instructions (goal-aware, per-section) ───────────────

    private static string BuildSectionInstructions(ProjectFusionGoal goal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Produce the following sections in your response, each clearly labeled:");
        sb.AppendLine();

        // PROJECT_IDENTITIES — always first
        sb.AppendLine(@"PROJECT_IDENTITIES:
For EACH input project, write a concise identity card:
- **What it is**: One sentence describing the project type and purpose.
- **Source**: The repo URL or local path from the input data.
- **Language / Framework**: Primary language and key frameworks (from input data ONLY).
- **Key capabilities**: 3-5 bullet points of what it can do (from strengths and architecture summary).
- **Maturity indicators**: Stars, forks, test count, dependency count — whatever is in the input.
Do NOT invent capabilities. Only describe what the input data evidences.");
        sb.AppendLine();

        // UNIFIED_VISION — varies by goal
        if (goal == ProjectFusionGoal.Compare)
        {
            sb.AppendLine(@"UNIFIED_VISION:
Write a comparison overview (2-3 paragraphs):
- What each project is designed for and its target audience.
- Key philosophical or architectural differences between them.
- When you would choose one over the other.");
        }
        else
        {
            sb.AppendLine(@"UNIFIED_VISION:
Write a 2-3 paragraph vision for the fused/resulting project:
- What is this new project? What problem does it solve?
- How do the input projects complement each other?
- What is the unique value proposition of combining them?");
        }
        sb.AppendLine();

        // ARCHITECTURE
        if (goal == ProjectFusionGoal.Compare)
        {
            sb.AppendLine(@"ARCHITECTURE:
Use a **comparison table** to contrast the architectures:
| Aspect | <Project A> | <Project B> |
|--------|-------------|-------------|
Then discuss key architectural differences and trade-offs in 2-3 paragraphs.");
        }
        else
        {
            sb.AppendLine(@"ARCHITECTURE:
Detailed architecture proposal (components, layers, data flow).
- Describe the proposed layer structure and how components from each input connect.
- Reference which input project each architectural decision draws from.
- Include a data flow description (what calls what, how data moves).");
        }
        sb.AppendLine();

        // TECH_STACK
        sb.AppendLine(@"TECH_STACK:
Key technology decisions with rationale. Use a table format:
| Technology | Purpose | Source Project(s) |
|-----------|---------|------------------|
CRITICAL: ONLY list technologies that appear in the input dependencies, frameworks, or languages.
Do NOT suggest or mention technologies not found in the input data.");
        sb.AppendLine();

        // FEATURE_MATRIX
        sb.AppendLine(@"FEATURE_MATRIX:
List features with source projects. Use this EXACT format (one per line):
FEATURE: <feature name> | SOURCE: <project name(s)>

Include all significant features from EACH input project.
For Compare mode, note which project(s) have each feature.");
        sb.AppendLine();

        // GAPS_CLOSED
        if (goal == ProjectFusionGoal.Compare)
        {
            sb.AppendLine(@"GAPS_CLOSED:
For each project, list the strengths it has that the OTHER project lacks.
Format: - **<Project>** fills gap: <description>");
        }
        else
        {
            sb.AppendLine(@"GAPS_CLOSED:
List specific gaps from the original projects that this fusion resolves.
Each item must reference which project HAD the gap and how the other project fills it.
Format: - <gap from Project A> → resolved by <capability from Project B>");
        }
        sb.AppendLine();

        // NEW_GAPS
        if (goal == ProjectFusionGoal.Compare)
        {
            sb.AppendLine(@"NEW_GAPS:
List gaps or weaknesses that BOTH projects share — things neither solves.
Also note areas where combining them would introduce friction or conflicts.");
        }
        else
        {
            sb.AppendLine(@"NEW_GAPS:
List any new gaps, challenges, or risks introduced by the fusion.
Be specific: integration complexity, version conflicts, architectural mismatches.
Only mention real technical concerns based on the input data.");
        }
        sb.AppendLine();

        // IP_NOTES
        sb.AppendLine(@"IP_NOTES:
Licensing and attribution analysis:
- List each project's license (from input data).
- Note compatibility concerns between licenses.
- Flag any attribution requirements.
Be specific to THESE projects — no generic legal boilerplate.");
        sb.AppendLine();

        // PROVENANCE
        sb.AppendLine(@"PROVENANCE:
For each major decision in this analysis, note which input(s) inspired it.
Use this EXACT format (one per line):
DECISION: <decision> | FROM: <project name(s)>");

        return sb.ToString();
    }

    // ─────────────── Section Expansion (goal-aware, section-specific prompts) ───────────────

    /// <summary>Expand a single fusion section from the outline, with truncation retry.</summary>
    private async Task<(string name, string content)> ExpandSectionAsync(
        string sectionName, ProjectFusionGoal goal, string outline, string inputContext,
        string goalInstruction, string systemPrompt, CancellationToken ct)
    {
        var sectionGuidance = GetSectionGuidance(sectionName, goal);

        var prompt = $@"You are expanding ONE section of a project fusion document.

Goal: {goalInstruction}

Here is the outline of all sections (for context):
{outline}

Here are the project inputs:
{inputContext}

Now write ONLY the **{sectionName}** section.

{sectionGuidance}

RULES:
- Output the section content directly — do NOT repeat the section name as a header.
- ONLY reference technologies, libraries, and capabilities that appear in the input data above.
- If you are unsure whether a technology is used, do NOT mention it.
- Be specific and concrete. No filler or generic statements.
- Use **bold** for key terms, markdown tables where comparing items, bullet points for lists.";

        var response = await _llmService.GenerateWithMetadataAsync(prompt, systemPrompt, 1500, ct: ct);

        // If truncated, retry with more tokens
        if (response.WasTruncated)
            response = await _llmService.GenerateWithMetadataAsync(prompt, systemPrompt, 3000, ct: ct);

        return (sectionName, response.Text);
    }

    /// <summary>Returns section-specific writing guidance that varies by goal.</summary>
    private static string GetSectionGuidance(string sectionName, ProjectFusionGoal goal) => sectionName switch
    {
        "PROJECT_IDENTITIES" =>
            "For EACH input project, write a concise identity card with: what it is (one sentence), source URL/path, primary language/framework, 3-5 key capabilities (from input data only), and maturity indicators (stars, tests, etc.).",

        "UNIFIED_VISION" when goal == ProjectFusionGoal.Compare =>
            "Write a comparison overview: what each project is designed for, key differences, when you'd choose one over the other. Do NOT propose merging them.",

        "UNIFIED_VISION" =>
            "Write a 2-3 paragraph vision: what this combined project IS, what problem it solves, how the inputs complement each other, and the unique value proposition.",

        "ARCHITECTURE" when goal == ProjectFusionGoal.Compare =>
            "Use a markdown comparison TABLE contrasting architectures (| Aspect | Project A | Project B |). Then discuss key differences and trade-offs.",

        "ARCHITECTURE" =>
            "Describe the proposed architecture: layers, components, data flow. Reference which input project each decision draws from. Include integration points.",

        "TECH_STACK" =>
            "Use a markdown TABLE (| Technology | Purpose | Source |). CRITICAL: ONLY list technologies from the input dependencies/frameworks/languages. Do NOT invent technologies.",

        "FEATURE_MATRIX" =>
            "List ALL significant features in the EXACT format: FEATURE: <name> | SOURCE: <project(s)>. One per line. Include features from EVERY input project.",

        "GAPS_CLOSED" when goal == ProjectFusionGoal.Compare =>
            "For each project, list strengths it has that the other lacks. Format: **<Project>** fills gap: <description>.",

        "GAPS_CLOSED" =>
            "List gaps from original projects resolved by fusion. Reference which project HAD the gap and which provides the fix. Be specific to the input data.",

        "NEW_GAPS" when goal == ProjectFusionGoal.Compare =>
            "List gaps BOTH projects share and areas where combining them would cause friction.",

        "NEW_GAPS" =>
            "List new gaps, risks, or challenges from the fusion. Be specific: integration complexity, conflicts, mismatches based on input data.",

        "IP_NOTES" =>
            "List each project's license from the input data. Note compatibility. Flag attribution requirements. No generic legal boilerplate.",

        "PROVENANCE" =>
            "For each major decision, use EXACT format: DECISION: <decision> | FROM: <project(s)>. One per line.",

        _ => "Be thorough, specific, and grounded in the input data."
    };

    // ─────────────── Input Formatting (comprehensive) ───────────────

    private static string FormatProfileForLlm(RepoProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Source:** {p.RepoUrl}");
        sb.AppendLine($"**Description:** {p.Description}");
        sb.AppendLine($"**Primary Language:** {p.PrimaryLanguage} | **All Languages:** {string.Join(", ", p.Languages)}");

        if (p.Frameworks.Count > 0)
            sb.AppendLine($"**Frameworks:** {string.Join(", ", p.Frameworks)}");

        sb.AppendLine($"**Stars:** {p.Stars:N0} | **Forks:** {p.Forks:N0} | **Open Issues:** {p.OpenIssues}");

        if (p.Topics.Count > 0)
            sb.AppendLine($"**Topics:** {string.Join(", ", p.Topics)}");

        if (p.LastCommitUtc.HasValue)
            sb.AppendLine($"**Last Commit:** {p.LastCommitUtc:yyyy-MM-dd}");

        // Dependencies — include version info, show all (up to 40)
        if (p.Dependencies.Count > 0)
        {
            sb.AppendLine($"**Dependencies ({p.Dependencies.Count}):**");
            foreach (var d in p.Dependencies.Take(40))
            {
                var ver = string.IsNullOrWhiteSpace(d.Version) ? "" : $" v{d.Version}";
                var lic = string.IsNullOrWhiteSpace(d.License) ? "" : $" [{d.License}]";
                sb.AppendLine($"  - {d.Name}{ver}{lic}");
            }
            if (p.Dependencies.Count > 40)
                sb.AppendLine($"  - ...and {p.Dependencies.Count - 40} more");
        }

        // Strengths
        if (p.Strengths.Count > 0)
        {
            sb.AppendLine("**Proven Strengths:**");
            foreach (var s in p.Strengths)
                sb.AppendLine($"  - {s}");
        }

        // Gaps
        if (p.Gaps.Count > 0)
        {
            sb.AppendLine("**Known Gaps:**");
            foreach (var g in p.Gaps)
                sb.AppendLine($"  - {g}");
        }

        // Complements
        if (p.ComplementSuggestions.Count > 0)
        {
            sb.AppendLine("**Complement Suggestions:**");
            foreach (var c in p.ComplementSuggestions.Take(10))
                sb.AppendLine($"  - {c.Name}: {c.Purpose}");
        }

        // File tree
        if (p.TopLevelEntries.Count > 0)
        {
            sb.AppendLine("**Top-Level File Structure:**");
            foreach (var e in p.TopLevelEntries.Take(20))
                sb.AppendLine($"  - {e.DisplayIcon} {e.Name}");
        }

        // CodeBook (architecture summary from scan) — truncate to keep prompt reasonable
        if (!string.IsNullOrWhiteSpace(p.CodeBook))
        {
            var codebook = p.CodeBook.Length > 2500
                ? p.CodeBook[..2500] + "\n  ...(truncated)"
                : p.CodeBook;
            sb.AppendLine("**Architecture Summary (from code analysis):**");
            sb.AppendLine(codebook);
        }

        return sb.ToString();
    }

    private static string FormatFusionForLlm(ProjectFusionArtifact f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Previous Fusion:** {f.Title}");
        sb.AppendLine($"**Goal:** {f.Goal}");
        sb.AppendLine($"**Inputs:** {f.InputSummary}");
        if (!string.IsNullOrWhiteSpace(f.ProjectIdentities))
        {
            sb.AppendLine("**Project Identities:**");
            sb.AppendLine(f.ProjectIdentities);
        }
        sb.AppendLine($"**Vision:** {f.UnifiedVision}");
        sb.AppendLine($"**Architecture:** {f.ArchitectureProposal}");
        sb.AppendLine($"**Tech Stack:** {f.TechStackDecisions}");
        if (f.FeatureMatrix.Count > 0)
            sb.AppendLine($"**Features:** {string.Join("; ", f.FeatureMatrix.Select(kv => $"{kv.Key} (from {kv.Value})"))}");
        if (f.GapsClosed.Count > 0)
            sb.AppendLine($"**Gaps Closed:** {string.Join("; ", f.GapsClosed)}");
        if (f.NewGaps.Count > 0)
            sb.AppendLine($"**Remaining Gaps:** {string.Join("; ", f.NewGaps)}");
        return sb.ToString();
    }

    private static string ExtractSection(string text, string sectionName)
    {
        // Find section header like "UNIFIED_VISION:" or "## UNIFIED_VISION" or "**UNIFIED_VISION**"
        var patterns = new[] { $"{sectionName}:", $"## {sectionName}", $"**{sectionName}**" };
        int startIdx = -1;
        int headerLen = 0;

        foreach (var pattern in patterns)
        {
            startIdx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (startIdx >= 0) { headerLen = pattern.Length; break; }
        }

        if (startIdx < 0) return string.Empty;

        startIdx += headerLen;

        // Find next section boundary
        var nextSections = AllSections;

        int endIdx = text.Length;
        foreach (var ns in nextSections)
        {
            if (ns.Equals(sectionName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var p in new[] { $"{ns}:", $"## {ns}", $"**{ns}**" })
            {
                var idx = text.IndexOf(p, startIdx, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx < endIdx) endIdx = idx;
            }
        }

        return text[startIdx..endIdx].Trim();
    }

    private static Dictionary<string, string> ParseFeatureMatrix(string text)
    {
        var section = ExtractSection(text, "FEATURE_MATRIX");
        var result = new Dictionary<string, string>();
        foreach (var line in section.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimStart('-', '*', ' ');
            // Parse "FEATURE: X | SOURCE: Y" or just "X | Y" or "X: Y"
            if (trimmed.StartsWith("FEATURE:", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed["FEATURE:".Length..].Trim();

            var pipeIdx = trimmed.IndexOf('|');
            if (pipeIdx > 0)
            {
                var feature = trimmed[..pipeIdx].Trim();
                var source = trimmed[(pipeIdx + 1)..].Trim();
                if (source.StartsWith("SOURCE:", StringComparison.OrdinalIgnoreCase))
                    source = source["SOURCE:".Length..].Trim();
                if (!string.IsNullOrEmpty(feature))
                    result[feature] = source;
            }
        }
        return result;
    }

    private static Dictionary<string, string> ParseProvenanceMap(string text)
    {
        var section = ExtractSection(text, "PROVENANCE");
        var result = new Dictionary<string, string>();
        foreach (var line in section.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimStart('-', '*', ' ');
            if (trimmed.StartsWith("DECISION:", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed["DECISION:".Length..].Trim();

            var pipeIdx = trimmed.IndexOf('|');
            if (pipeIdx > 0)
            {
                var decision = trimmed[..pipeIdx].Trim();
                var from = trimmed[(pipeIdx + 1)..].Trim();
                if (from.StartsWith("FROM:", StringComparison.OrdinalIgnoreCase))
                    from = from["FROM:".Length..].Trim();
                if (!string.IsNullOrEmpty(decision))
                    result[decision] = from;
            }
        }
        return result;
    }

    private static List<string> ParseList(string text)
    {
        var items = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimStart('-', '*', '•', ' ');
            if (!string.IsNullOrWhiteSpace(trimmed))
                items.Add(trimmed);
        }
        return items;
    }

    private static IpAssessment? ParseIpNotes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new IpAssessment
        {
            Notes = text,
            UncertaintyLevel = "Review Required"
        };
    }

    private static string GenerateReport(ProjectFusionArtifact a, List<string> inputTitles, List<string> inputUrls)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {a.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Goal:** {GoalDescription(a.Goal)}");
        sb.AppendLine($"**Created:** {a.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        // Source references header
        sb.AppendLine("## Source Projects");
        for (int i = 0; i < inputTitles.Count; i++)
        {
            var url = i < inputUrls.Count ? inputUrls[i] : "(unknown)";
            sb.AppendLine($"- **{inputTitles[i]}** — {url}");
        }
        sb.AppendLine();

        // Project Identities
        if (!string.IsNullOrWhiteSpace(a.ProjectIdentities))
        {
            sb.AppendLine("## Project Identities");
            sb.AppendLine(a.ProjectIdentities);
            sb.AppendLine();
        }

        // Vision / Comparison Summary
        var visionHeading = a.Goal == ProjectFusionGoal.Compare ? "Comparison Overview" : "Unified Vision";
        sb.AppendLine($"## {visionHeading}");
        sb.AppendLine(a.UnifiedVision);
        sb.AppendLine();

        // Architecture
        var archHeading = a.Goal == ProjectFusionGoal.Compare ? "Architecture Comparison" : "Architecture Proposal";
        sb.AppendLine($"## {archHeading}");
        sb.AppendLine(a.ArchitectureProposal);
        sb.AppendLine();

        // Tech Stack
        var techHeading = a.Goal == ProjectFusionGoal.Compare ? "Technology Comparison" : "Tech Stack Decisions";
        sb.AppendLine($"## {techHeading}");
        sb.AppendLine(a.TechStackDecisions);
        sb.AppendLine();

        // Feature Matrix
        if (a.FeatureMatrix.Count > 0)
        {
            sb.AppendLine("## Feature Matrix");
            sb.AppendLine("| Feature | Source |");
            sb.AppendLine("|---------|--------|");
            foreach (var kv in a.FeatureMatrix)
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
        }

        // Gaps Closed / Strengths
        if (a.GapsClosed.Count > 0)
        {
            var gapHeading = a.Goal == ProjectFusionGoal.Compare ? "Complementary Strengths" : "Gaps Closed";
            sb.AppendLine($"## {gapHeading}");
            foreach (var g in a.GapsClosed)
                sb.AppendLine($"- ✅ {g}");
            sb.AppendLine();
        }

        // New Gaps / Shared Weaknesses
        if (a.NewGaps.Count > 0)
        {
            var newGapHeading = a.Goal == ProjectFusionGoal.Compare ? "Shared Gaps & Potential Conflicts" : "New Gaps / Challenges";
            sb.AppendLine($"## {newGapHeading}");
            foreach (var g in a.NewGaps)
                sb.AppendLine($"- {g}");
            sb.AppendLine();
        }

        // IP Notes
        if (a.IpNotes != null)
        {
            sb.AppendLine("## IP & Licensing Notes");
            sb.AppendLine(a.IpNotes.Notes);
            sb.AppendLine();
        }

        // Provenance
        if (a.ProvenanceMap.Count > 0)
        {
            var provHeading = a.Goal == ProjectFusionGoal.Compare ? "Recommendation Map" : "Provenance Map";
            sb.AppendLine($"## {provHeading}");
            sb.AppendLine("| Decision | Source(s) |");
            sb.AppendLine("|----------|-----------|");
            foreach (var kv in a.ProvenanceMap)
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AddReplay(ResearchJob job, string type, string title, string description)
    {
        job.ReplayEntries.Add(new ReplayEntry
        {
            Order = job.ReplayEntries.Count + 1,
            Title = title, Description = description, EntryType = type
        });
    }
}
