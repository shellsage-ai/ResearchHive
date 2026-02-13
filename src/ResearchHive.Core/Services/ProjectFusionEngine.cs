using ResearchHive.Core.Models;
using System.Text;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Fuses multiple RepoProfiles (or prior ProjectFusionArtifacts) into a unified
/// architecture / merge / extension / comparison document with full provenance.
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
                    }
                }
            }

            AddReplay(job, "gather", "Inputs Loaded", $"Loaded {inputBlocks.Count} inputs: {string.Join(", ", inputTitles)}");

            if (inputBlocks.Count == 0)
                throw new InvalidOperationException("No valid inputs found for fusion.");

            // 3. Build the fusion prompt
            job.State = JobState.Drafting;
            db.SaveJob(job);

            var goalInstruction = request.Goal switch
            {
                ProjectFusionGoal.Merge =>
                    "Merge these projects into one unified project. Combine their strengths, resolve overlapping features, and produce a single cohesive architecture.",
                ProjectFusionGoal.Extend =>
                    "Extend the first project by integrating capabilities from the others. The first project is the base; others provide extensions, plugins, or new features.",
                ProjectFusionGoal.Compare =>
                    "Compare these projects side-by-side. Analyze their architectures, tech stacks, strengths, and weaknesses. Produce a comparison matrix and recommendations.",
                ProjectFusionGoal.Architect =>
                    "Design a new architecture that draws the best ideas from all inputs. You are not merging code — you are designing a new system inspired by these projects.",
                _ => "Analyze and fuse these project inputs."
            };

            var systemPrompt = @"You are a senior software architect specializing in project fusion and system design.
You produce detailed, actionable architecture documents with provenance tracking.
Every claim or decision you make should reference which input project(s) it draws from.";

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

            userPrompt.AppendLine(@"Produce the following sections in your response, each clearly labeled:

UNIFIED_VISION: A 2-3 paragraph vision for the fused project.

ARCHITECTURE: A detailed architecture proposal (components, layers, data flow).

TECH_STACK: Key technology decisions with rationale, referencing which input(s) each comes from.

FEATURE_MATRIX: A list of features in the format:
FEATURE: <feature name> | SOURCE: <input name(s)>
(one per line)

GAPS_CLOSED: List gaps from the original projects that this fusion resolves.

NEW_GAPS: List any new gaps or challenges introduced by the fusion.

IP_NOTES: Any licensing, IP, or attribution concerns from combining these projects.

PROVENANCE: For each major architectural decision, note which input(s) inspired it in the format:
DECISION: <decision> | FROM: <input name(s)>
(one per line)");

            AddReplay(job, "fuse", "Generating Fusion Outline", $"Sending {inputBlocks.Count} inputs to LLM for section outline");

            // --- Outline-then-expand: Call 1 — get a structured outline, Call 2-9 — expand each section ---
            var sectionNames = new[] { "UNIFIED_VISION", "ARCHITECTURE", "TECH_STACK", "FEATURE_MATRIX",
                                       "GAPS_CLOSED", "NEW_GAPS", "IP_NOTES", "PROVENANCE" };

            var outlinePrompt = new StringBuilder();
            outlinePrompt.AppendLine(userPrompt.ToString());
            outlinePrompt.AppendLine();
            outlinePrompt.AppendLine("IMPORTANT: For now, produce ONLY a bullet-point outline of each section (3-6 bullets per section).");
            outlinePrompt.AppendLine("Do NOT write full prose yet. Just key points, decisions, and items for each section.");
            outlinePrompt.AppendLine("Label each section clearly.");

            var outlineResponse = await _llmService.GenerateAsync(outlinePrompt.ToString(), systemPrompt, 1500, ct: ct);
            AddReplay(job, "outline", "Outline Generated", "Expanding individual sections...");

            // Build shared context block (compact version of inputs for section calls)
            var compactContext = new StringBuilder();
            for (int i = 0; i < inputBlocks.Count; i++)
                compactContext.AppendLine($"## Input {i + 1}: {inputTitles[i]}\n{inputBlocks[i]}");
            var contextStr = compactContext.ToString();

            // Expand sections in parallel batches of 4
            var sectionResults = new Dictionary<string, string>();
            for (int batch = 0; batch < sectionNames.Length; batch += 4)
            {
                var tasks = new List<Task<(string name, string content)>>();
                for (int j = batch; j < Math.Min(batch + 4, sectionNames.Length); j++)
                {
                    var sectionName = sectionNames[j];
                    tasks.Add(ExpandSectionAsync(sectionName, outlineResponse, contextStr, goalInstruction, systemPrompt, ct));
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
            foreach (var section in sectionNames)
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

            var report = GenerateReport(artifact);
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

    /// <summary>Expand a single fusion section from the outline, with truncation retry.</summary>
    private async Task<(string name, string content)> ExpandSectionAsync(
        string sectionName, string outline, string inputContext, string goalInstruction,
        string systemPrompt, CancellationToken ct)
    {
        var prompt = $@"You are expanding ONE section of a project fusion document.

Goal: {goalInstruction}

Here is the outline of all sections (for context):
{outline}

Here are the project inputs:
{inputContext}

Now write ONLY the **{sectionName}** section in full detail. Be thorough and specific.
Output the section content directly — do NOT repeat the section name as a header.";

        var response = await _llmService.GenerateWithMetadataAsync(prompt, systemPrompt, 1500, ct: ct);
        
        // If truncated, retry with more tokens
        if (response.WasTruncated)
            response = await _llmService.GenerateWithMetadataAsync(prompt, systemPrompt, 3000, ct: ct);

        return (sectionName, response.Text);
    }

    private static string FormatProfileForLlm(RepoProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Repo:** {p.RepoUrl}");
        sb.AppendLine($"**Language:** {p.PrimaryLanguage} | **Languages:** {string.Join(", ", p.Languages)}");
        sb.AppendLine($"**Frameworks:** {string.Join(", ", p.Frameworks)}");
        sb.AppendLine($"**Stars:** {p.Stars} | **Forks:** {p.Forks}");
        sb.AppendLine($"**Description:** {p.Description}");
        if (p.Dependencies.Count > 0)
            sb.AppendLine($"**Dependencies ({p.Dependencies.Count}):** {string.Join(", ", p.Dependencies.Take(20).Select(d => d.Name))}");
        sb.AppendLine($"**Strengths:** {string.Join("; ", p.Strengths)}");
        sb.AppendLine($"**Gaps:** {string.Join("; ", p.Gaps)}");
        if (p.ComplementSuggestions.Count > 0)
            sb.AppendLine($"**Complement Suggestions:** {string.Join("; ", p.ComplementSuggestions.Select(c => $"{c.Name} ({c.Purpose})"))}");
        return sb.ToString();
    }

    private static string FormatFusionForLlm(ProjectFusionArtifact f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Previous Fusion:** {f.Title}");
        sb.AppendLine($"**Goal:** {f.Goal}");
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
        // Find section header like "UNIFIED_VISION:" or "## UNIFIED_VISION"
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
        var nextSections = new[] { "UNIFIED_VISION", "ARCHITECTURE", "TECH_STACK", "FEATURE_MATRIX",
                                    "GAPS_CLOSED", "NEW_GAPS", "IP_NOTES", "PROVENANCE" };

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

    private static string GenerateReport(ProjectFusionArtifact a)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {a.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Goal:** {a.Goal} | **Inputs:** {a.InputSummary}");
        sb.AppendLine($"**Created:** {a.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        sb.AppendLine("## Unified Vision");
        sb.AppendLine(a.UnifiedVision);
        sb.AppendLine();

        sb.AppendLine("## Architecture Proposal");
        sb.AppendLine(a.ArchitectureProposal);
        sb.AppendLine();

        sb.AppendLine("## Tech Stack Decisions");
        sb.AppendLine(a.TechStackDecisions);
        sb.AppendLine();

        if (a.FeatureMatrix.Count > 0)
        {
            sb.AppendLine("## Feature Matrix");
            sb.AppendLine("| Feature | Source |");
            sb.AppendLine("|---------|--------|");
            foreach (var kv in a.FeatureMatrix)
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
        }

        if (a.GapsClosed.Count > 0)
        {
            sb.AppendLine("## Gaps Closed");
            foreach (var g in a.GapsClosed)
                sb.AppendLine($"- ✅ {g}");
            sb.AppendLine();
        }

        if (a.NewGaps.Count > 0)
        {
            sb.AppendLine("## New Gaps / Challenges");
            foreach (var g in a.NewGaps)
                sb.AppendLine($"- 🔸 {g}");
            sb.AppendLine();
        }

        if (a.IpNotes != null)
        {
            sb.AppendLine("## IP & Licensing Notes");
            sb.AppendLine(a.IpNotes.Notes);
            sb.AppendLine();
        }

        if (a.ProvenanceMap.Count > 0)
        {
            sb.AppendLine("## Provenance Map");
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
