using ResearchHive.Core.Models;
using System.Text;

namespace ResearchHive.Core.Services;

/// <summary>
/// Orchestrates repo scanning + complement research as a tracked job.
/// Also supports multi-repo instant scan-and-fuse.
/// </summary>
public class RepoIntelligenceJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly RepoScannerService _scanner;
    private readonly ComplementResearchService _complementService;
    private readonly LlmService _llmService;
    private readonly RepoIndexService? _repoIndexService;
    private readonly CodeBookGenerator? _codeBookGenerator;
    private readonly RetrievalService? _retrievalService;

    // Optional Hive Mind integration — set via property injection
    public GlobalMemoryService? GlobalMemory { get; set; }

    public RepoIntelligenceJobRunner(
        SessionManager sessionManager,
        RepoScannerService scanner,
        ComplementResearchService complementService,
        LlmService llmService,
        RepoIndexService? repoIndexService = null,
        CodeBookGenerator? codeBookGenerator = null,
        RetrievalService? retrievalService = null)
    {
        _sessionManager = sessionManager;
        _scanner = scanner;
        _complementService = complementService;
        _llmService = llmService;
        _repoIndexService = repoIndexService;
        _codeBookGenerator = codeBookGenerator;
        _retrievalService = retrievalService;
    }

    /// <summary>Scan a single repo: metadata → clone+index → RAG-grounded analysis → gap verification → complements.</summary>
    public async Task<RepoProfile> RunAnalysisAsync(string sessionId, string repoUrl, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.RepoAnalysis,
            Prompt = $"Analyze repository: {repoUrl}",
            State = JobState.Planning
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Repo Analysis Started", $"Target: {repoUrl}");

        try
        {
            // ── Phase 1: Metadata scan (GitHub API — no LLM yet) ──
            job.State = JobState.Searching;
            db.SaveJob(job);
            AddReplay(job, "scan", "Scanning Repository", "Fetching metadata, README, dependencies via GitHub API...");

            var profile = await _scanner.ScanAsync(repoUrl, ct);
            profile.SessionId = sessionId;

            AddReplay(job, "scanned", "Metadata Collected",
                $"Found: {profile.PrimaryLanguage} | {profile.Dependencies.Count} deps | {profile.Stars}★");

            // ── Phase 2: Clone + deep index (chunks + embeddings) ──
            bool isIndexed = false;
            if (_repoIndexService != null)
            {
                try
                {
                    AddReplay(job, "indexing", "Indexing Repository Code", "Cloning repo and building vector index of all source files...");
                    await _repoIndexService.IndexRepoAsync(sessionId, profile, ct);
                    isIndexed = profile.IndexedChunkCount > 0;
                    AddReplay(job, "indexed", "Code Indexed",
                        $"Indexed {profile.IndexedFileCount} files → {profile.IndexedChunkCount} chunks");
                }
                catch (Exception ex)
                {
                    AddReplay(job, "index_warn", "Index Skipped", $"Could not index code: {ex.Message}");
                }
            }

            // ── Phase 3: CodeBook generation (architecture summary from chunks) ──
            if (_codeBookGenerator != null && isIndexed)
            {
                try
                {
                    AddReplay(job, "codebook", "Generating CodeBook", "Analyzing architecture patterns from indexed code...");
                    profile.CodeBook = await _codeBookGenerator.GenerateAsync(sessionId, profile, ct);
                    AddReplay(job, "codebook_done", "CodeBook Generated", "Architecture reference document created");
                }
                catch (Exception ex)
                {
                    AddReplay(job, "codebook_warn", "CodeBook Skipped", $"Could not generate CodeBook: {ex.Message}");
                }
            }

            // ── Phase 4: RAG-grounded strengths + gaps analysis ──
            job.State = JobState.Evaluating;
            db.SaveJob(job);

            if (isIndexed && _retrievalService != null)
            {
                AddReplay(job, "rag_analysis", "RAG-Grounded Analysis", "Querying indexed code to build comprehensive assessment...");
                await RunRagGroundedAnalysis(sessionId, profile, ct);
            }
            else
            {
                // Fallback: shallow analysis from metadata only (no code available)
                AddReplay(job, "shallow_analysis", "Shallow Analysis", "No indexed code available — analyzing from README + manifests only");
                await _scanner.AnalyzeShallowAsync(profile, ct);
            }

            AddReplay(job, "analyzed", "Analysis Complete",
                $"{profile.Strengths.Count} strengths, {profile.Gaps.Count} gaps identified");

            // ── Phase 5: Gap verification via RAG (prune false positives) ──
            if (isIndexed && _retrievalService != null && profile.Gaps.Count > 0)
            {
                AddReplay(job, "gap_verify", "Verifying Gaps Against Code", "Checking each gap claim against actual source code...");
                var originalGapCount = profile.Gaps.Count;
                await VerifyGapsViaRag(sessionId, profile, ct);
                var pruned = originalGapCount - profile.Gaps.Count;
                if (pruned > 0)
                    AddReplay(job, "gaps_pruned", "False Positives Removed", $"Removed {pruned} false gaps — {profile.Gaps.Count} verified gaps remain");
                else
                    AddReplay(job, "gaps_confirmed", "Gaps Confirmed", $"All {profile.Gaps.Count} gaps verified as genuine");
            }

            // ── Phase 6: Complement research (based on verified gaps) ──
            AddReplay(job, "complement", "Researching Complements", $"Searching for projects to fill {profile.Gaps.Count} verified gaps...");
            var complements = await _complementService.ResearchAsync(profile, ct);
            profile.ComplementSuggestions = complements;
            AddReplay(job, "complements_done", "Complements Found", $"Found {complements.Count} complementary projects");

            // ── Phase 7: Save + report ──
            db.SaveRepoProfile(profile);

            job.State = JobState.Reporting;
            db.SaveJob(job);

            var report = GenerateReport(profile);
            var reportRecord = new Report
            {
                SessionId = sessionId,
                JobId = job.Id,
                ReportType = "RepoAnalysis",
                Title = $"Repo Analysis: {profile.Owner}/{profile.Name}",
                Content = report,
                Format = "markdown"
            };
            db.SaveReport(reportRecord);

            job.State = JobState.Completed;
            job.FullReport = report;
            job.ExecutiveSummary = $"Analyzed {profile.Owner}/{profile.Name}: {profile.PrimaryLanguage}, {profile.Dependencies.Count} dependencies, {profile.IndexedChunkCount} chunks indexed, {profile.Strengths.Count} strengths, {profile.Gaps.Count} verified gaps, {complements.Count} complement suggestions.";
            db.SaveJob(job);
            AddReplay(job, "complete", "Analysis Complete", job.ExecutiveSummary);
            db.SaveJob(job);

            return profile;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            db.SaveJob(job);
            throw;
        }
    }

    /// <summary>
    /// Multi-query RAG analysis: issue diverse queries against the indexed codebase,
    /// gather top chunks, and ask the LLM to assess strengths/gaps from actual code.
    /// </summary>
    private async Task RunRagGroundedAnalysis(string sessionId, RepoProfile profile, CancellationToken ct)
    {
        // Diverse queries to explore the full codebase — not just architecture
        var analysisQueries = new[]
        {
            "service registration dependency injection startup configuration",
            "database data access storage persistence repository",
            "API cloud provider external service integration",
            "test testing unit test integration test assertion",
            "error handling retry fault tolerance resilience",
            "user interface view model MVVM commands controls",
            "search indexing retrieval embedding vector",
            "authentication security encryption key management",
            "export reporting document generation output",
            "domain business logic core workflow pipeline",
            "notification monitoring health check logging",
            "safety validation verification quality"
        };

        var repoFilter = new[] { "repo_code", "repo_doc" };
        var allChunks = new List<RetrievalResult>();

        foreach (var q in analysisQueries)
        {
            try
            {
                var hits = await _retrievalService!.HybridSearchAsync(sessionId, q, repoFilter, topK: 5, ct);
                allChunks.AddRange(hits);
            }
            catch { /* individual query failures are non-fatal */ }
        }

        // Deduplicate and take top 30 by score — broader coverage than CodeBook's 20
        var topChunks = allChunks
            .DistinctBy(r => r.Chunk.Id)
            .OrderByDescending(r => r.Score)
            .Take(30)
            .Select(r => r.Chunk.Text)
            .ToList();

        if (topChunks.Count == 0)
        {
            // Fallback to shallow
            await _scanner.AnalyzeShallowAsync(profile, ct);
            return;
        }

        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, profile.CodeBook, topChunks);
        var analysis = await _llmService.GenerateAsync(prompt,
            "You are a senior software architecture analyst. Analyze the ACTUAL SOURCE CODE provided. " +
            "Be specific — reference real class names, service names, and patterns you see in the code. " +
            "Do NOT make generic assumptions. If the code shows cloud providers, say so. If it has 300+ tests, say so.",
            ct: ct);
        RepoScannerService.ParseAnalysis(analysis, profile);
    }

    /// <summary>
    /// For each proposed gap, query the index with gap-related terms.
    /// If relevant code is found, the gap might be a false positive.
    /// Send all evidence to LLM for final verification.
    /// </summary>
    private async Task VerifyGapsViaRag(string sessionId, RepoProfile profile, CancellationToken ct)
    {
        var repoFilter = new[] { "repo_code", "repo_doc" };
        var gapEvidence = new List<(string gap, IReadOnlyList<string> chunks)>();

        foreach (var gap in profile.Gaps)
        {
            try
            {
                // Search the indexed codebase for evidence related to this gap
                var hits = await _retrievalService!.HybridSearchAsync(sessionId, gap, repoFilter, topK: 3, ct);
                var chunkTexts = hits.Select(h => h.Chunk.Text).ToList();
                gapEvidence.Add((gap, chunkTexts));
            }
            catch
            {
                gapEvidence.Add((gap, Array.Empty<string>()));
            }
        }

        var verificationPrompt = RepoScannerService.BuildGapVerificationPrompt(profile, gapEvidence);
        var response = await _llmService.GenerateAsync(verificationPrompt,
            "You are a code auditor. Compare each gap claim against the actual source code evidence. " +
            "Be strict: if the code clearly already handles something, mark it as a false positive. " +
            "Only keep gaps that are genuinely missing from the codebase.",
            ct: ct);

        var verifiedGaps = RepoScannerService.ParseVerifiedGaps(response);
        if (verifiedGaps.Count > 0)
            profile.Gaps = verifiedGaps;
        // If parsing returned empty (LLM format issue), keep original gaps rather than losing them all
    }

    /// <summary>Scan multiple repos and immediately fuse them.</summary>
    public async Task<(List<RepoProfile> profiles, ProjectFusionArtifact fusion)> RunMultiScanFusionAsync(
        string sessionId, List<string> repoUrls, string focusPrompt, ProjectFusionGoal goal,
        ProjectFusionEngine fusionEngine, CancellationToken ct = default)
    {
        // Scan all repos
        var profiles = new List<RepoProfile>();
        foreach (var url in repoUrls)
        {
            if (ct.IsCancellationRequested) break;
            var profile = await RunAnalysisAsync(sessionId, url, ct);
            profiles.Add(profile);
        }

        // Build fusion request from all profiles
        var request = new ProjectFusionRequest
        {
            SessionId = sessionId,
            Goal = goal,
            FocusPrompt = focusPrompt,
            Inputs = profiles.Select(p => new ProjectFusionInput
            {
                Id = p.Id,
                Type = FusionInputType.RepoProfile,
                Title = $"{p.Owner}/{p.Name}"
            }).ToList()
        };

        var fusion = await fusionEngine.RunAsync(sessionId, request, ct);
        return (profiles, fusion);
    }

    private static string GenerateReport(RepoProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Repository Analysis: {p.Owner}/{p.Name}");
        sb.AppendLine();
        sb.AppendLine($"**URL:** {p.RepoUrl}");
        sb.AppendLine($"**Primary Language:** {p.PrimaryLanguage}");
        sb.AppendLine($"**Stars:** {p.Stars} | **Forks:** {p.Forks} | **Open Issues:** {p.OpenIssues}");
        if (p.Topics.Count > 0)
            sb.AppendLine($"**Topics:** {string.Join(", ", p.Topics)}");
        if (p.LastCommitUtc.HasValue)
            sb.AppendLine($"**Last Updated:** {p.LastCommitUtc.Value:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine($"> {p.Description}");
        sb.AppendLine();

        if (p.Languages.Count > 0)
        {
            sb.AppendLine("## Languages");
            foreach (var lang in p.Languages)
                sb.AppendLine($"- {lang}");
            sb.AppendLine();
        }

        if (p.Frameworks.Count > 0)
        {
            sb.AppendLine("## Frameworks & Key Technologies");
            foreach (var fw in p.Frameworks)
                sb.AppendLine($"- {fw}");
            sb.AppendLine();
        }

        if (p.Dependencies.Count > 0)
        {
            sb.AppendLine("## Dependencies");
            sb.AppendLine("| Package | Version | Manifest |");
            sb.AppendLine("|---------|---------|----------|");
            foreach (var dep in p.Dependencies.Take(30))
                sb.AppendLine($"| {dep.Name} | {dep.Version} | {dep.ManifestFile} |");
            if (p.Dependencies.Count > 30)
                sb.AppendLine($"_...and {p.Dependencies.Count - 30} more_");
            sb.AppendLine();
        }

        sb.AppendLine("## Strengths");
        foreach (var s in p.Strengths)
            sb.AppendLine($"- ✅ {s}");
        sb.AppendLine();

        sb.AppendLine("## Gaps & Improvement Opportunities");
        foreach (var g in p.Gaps)
            sb.AppendLine($"- 🔸 {g}");
        sb.AppendLine();

        if (p.ComplementSuggestions.Count > 0)
        {
            sb.AppendLine("## Complementary Projects");
            sb.AppendLine();
            foreach (var c in p.ComplementSuggestions)
            {
                sb.AppendLine($"### {c.Name}");
                sb.AppendLine($"- **URL:** {c.Url}");
                sb.AppendLine($"- **Purpose:** {c.Purpose}");
                sb.AppendLine($"- **What it adds:** {c.WhatItAdds}");
                sb.AppendLine($"- **Category:** {c.Category} | **License:** {c.License} | **Maturity:** {c.Maturity}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Ask a natural-language question about a GitHub repo.
    /// Uses retrieval-augmented generation (RAG) when code is indexed,
    /// falling back to profile-only context otherwise.
    /// </summary>
    public async Task<string> AskAboutRepoAsync(string sessionId, string repoUrl, string question, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        // Find or create profile
        var profiles = db.GetRepoProfiles();
        var (owner, repo) = RepoScannerService.ParseRepoUrl(repoUrl);
        var profile = profiles.FirstOrDefault(p =>
            p.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
            p.Name.Equals(repo, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
        {
            // Quick scan (will also index if services are wired)
            profile = await RunAnalysisAsync(sessionId, repoUrl, ct);
        }

        // Build context from profile metadata
        var context = new StringBuilder();
        context.AppendLine($"Repository: {profile.Owner}/{profile.Name}");
        context.AppendLine($"URL: {profile.RepoUrl}");
        context.AppendLine($"Description: {profile.Description}");
        context.AppendLine($"Primary Language: {profile.PrimaryLanguage}");
        context.AppendLine($"Languages: {string.Join(", ", profile.Languages)}");
        context.AppendLine($"Frameworks: {string.Join(", ", profile.Frameworks)}");
        context.AppendLine($"Stars: {profile.Stars} | Forks: {profile.Forks} | Open Issues: {profile.OpenIssues}");
        context.AppendLine($"Topics: {string.Join(", ", profile.Topics)}");
        if (profile.LastCommitUtc != null)
        {
            var span = DateTime.UtcNow - profile.LastCommitUtc.Value;
            var ago = span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago" :
                      span.TotalDays < 30 ? $"{(int)span.TotalDays}d ago" :
                      span.TotalDays < 365 ? $"{(int)(span.TotalDays / 30)}mo ago" : $"{span.TotalDays / 365:F1}y ago";
            context.AppendLine($"Last push: {profile.LastCommitUtc.Value:yyyy-MM-dd} ({ago})");
        }
        if (profile.TopLevelEntries.Count > 0)
            context.AppendLine($"Root entries: {string.Join(", ", profile.TopLevelEntries.Select(e => $"{e.Name} ({e.Type})"))}");
        if (profile.Dependencies.Count > 0)
            context.AppendLine($"Dependencies ({profile.Dependencies.Count}): {string.Join(", ", profile.Dependencies.Take(30).Select(d => $"{d.Name} {d.Version}"))}");
        context.AppendLine($"Strengths: {string.Join("; ", profile.Strengths)}");
        context.AppendLine($"Gaps: {string.Join("; ", profile.Gaps)}");
        if (profile.ComplementSuggestions.Count > 0)
            context.AppendLine($"Complementary Projects: {string.Join("; ", profile.ComplementSuggestions.Select(c => $"{c.Name} — {c.Purpose}"))}");

        // Include CodeBook if available
        if (!string.IsNullOrEmpty(profile.CodeBook))
        {
            context.AppendLine($"\n--- CODEBOOK SUMMARY ---\n{profile.CodeBook}");
        }

        // RAG: retrieve relevant code chunks if indexed
        if (_retrievalService != null && profile.IndexedChunkCount > 0)
        {
            var repoFilter = new[] { "repo_code", "repo_doc" };
            var hits = await _retrievalService.HybridSearchAsync(sessionId, question, repoFilter, topK: 8, ct);
            if (hits.Count > 0)
            {
                context.AppendLine("\n--- RELEVANT CODE EXCERPTS ---");
                foreach (var hit in hits)
                {
                    context.AppendLine($"\n[{hit.Chunk.SourceType}] (score: {hit.Score:F3})");
                    context.AppendLine(hit.Chunk.Text);
                    context.AppendLine("---");
                }
            }
        }
        else if (!string.IsNullOrEmpty(profile.ReadmeContent))
        {
            // Fallback: include README if no code is indexed
            context.AppendLine($"\nREADME:\n{profile.ReadmeContent}");
        }

        var systemPrompt = @"You are a senior software engineer and open-source analyst.
You have deep knowledge of software architecture, ecosystems, dependencies, and project strategy.
Answer questions about the given repository thoroughly, with specific, actionable insights.
Reference concrete details from the repo data provided — class names, function names, file paths, dependencies.
When code excerpts are available, cite specific code patterns, classes, or functions from them.
If a CodeBook summary is provided, use it to ground your architectural analysis.
Be detailed and insightful — don't give surface-level answers.";

        var userPrompt = $@"Based on this repository profile and code:

{context}

User's question: {question}

Provide a thorough, detailed answer.";

        return await _llmService.GenerateAsync(userPrompt, systemPrompt, 3000, ct);
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
