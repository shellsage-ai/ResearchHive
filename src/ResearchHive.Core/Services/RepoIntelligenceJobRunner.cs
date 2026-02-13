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

    /// <summary>Scan a single repo: metadata + LLM analysis + complement research.</summary>
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
            // 1. Scan repo via GitHub API + LLM analysis
            job.State = JobState.Searching;
            db.SaveJob(job);
            AddReplay(job, "scan", "Scanning Repository", "Fetching metadata, README, dependencies via GitHub API...");

            var profile = await _scanner.ScanAsync(repoUrl, ct);
            profile.SessionId = sessionId;

            AddReplay(job, "scanned", "Repository Scanned",
                $"Found: {profile.PrimaryLanguage} | {profile.Dependencies.Count} deps | {profile.Stars}★ | {profile.Strengths.Count} strengths | {profile.Gaps.Count} gaps");

            // 2. Research complements for identified gaps
            job.State = JobState.Evaluating;
            db.SaveJob(job);
            AddReplay(job, "complement", "Researching Complements", $"Searching for projects to fill {profile.Gaps.Count} identified gaps...");

            var complements = await _complementService.ResearchAsync(profile, ct);
            profile.ComplementSuggestions = complements;

            AddReplay(job, "complements_done", "Complements Found", $"Found {complements.Count} complementary projects");

            // 3. Clone + index repo code (if RepoIndexService is available)
            if (_repoIndexService != null)
            {
                try
                {
                    AddReplay(job, "indexing", "Indexing Repository Code", "Cloning repo and building vector index of source files...");
                    await _repoIndexService.IndexRepoAsync(sessionId, profile, ct);
                    AddReplay(job, "indexed", "Code Indexed",
                        $"Indexed {profile.IndexedFileCount} files → {profile.IndexedChunkCount} chunks");
                }
                catch (Exception ex)
                {
                    AddReplay(job, "index_warn", "Index Skipped", $"Could not index code: {ex.Message}");
                }
            }

            // 4. Generate CodeBook (if available and indexed)
            if (_codeBookGenerator != null && profile.IndexedChunkCount > 0)
            {
                try
                {
                    AddReplay(job, "codebook", "Generating CodeBook", "Analyzing architecture patterns...");
                    profile.CodeBook = await _codeBookGenerator.GenerateAsync(sessionId, profile, ct);
                    AddReplay(job, "codebook_done", "CodeBook Generated", "Architecture reference document created");
                }
                catch (Exception ex)
                {
                    AddReplay(job, "codebook_warn", "CodeBook Skipped", $"Could not generate CodeBook: {ex.Message}");
                }
            }

            // 5. Save profile
            db.SaveRepoProfile(profile);

            // 6. Generate report
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
            job.ExecutiveSummary = $"Analyzed {profile.Owner}/{profile.Name}: {profile.PrimaryLanguage}, {profile.Dependencies.Count} dependencies, {profile.Strengths.Count} strengths, {profile.Gaps.Count} gaps, {complements.Count} complement suggestions.";
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
            var codeBook = profile.CodeBook.Length > 4000 ? profile.CodeBook[..4000] : profile.CodeBook;
            context.AppendLine($"\n--- CODEBOOK SUMMARY ---\n{codeBook}");
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
            var readme = profile.ReadmeContent.Length > 6000 ? profile.ReadmeContent[..6000] : profile.ReadmeContent;
            context.AppendLine($"\nREADME:\n{readme}");
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
