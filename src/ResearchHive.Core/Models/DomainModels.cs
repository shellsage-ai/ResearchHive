namespace ResearchHive.Core.Models;

public enum SafetyLevel { Low, Medium, High, Extreme }

public class SafetyAssessment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SessionId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public SafetyLevel Level { get; set; } = SafetyLevel.Low;
    public string RecommendedEnvironment { get; set; } = "Desk";
    public List<string> MinimumPPE { get; set; } = new();
    public List<string> Hazards { get; set; } = new();
    public List<string> DisposalNotes { get; set; } = new();
    public List<string> References { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
}

public class IpAssessment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SessionId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string LicenseSignal { get; set; } = string.Empty;
    public string UncertaintyLevel { get; set; } = "Unknown";
    public List<string> RiskFlags { get; set; } = new();
    public List<string> DesignAroundOptions { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
}

// Discovery Studio models
public class IdeaCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Hypothesis { get; set; } = string.Empty;
    public string Mechanism { get; set; } = string.Empty;
    public string MinimalTestPlan { get; set; } = string.Empty;
    public List<string> Risks { get; set; } = new();
    public string Falsification { get; set; } = string.Empty;
    public string? NoveltyCheck { get; set; }
    public string? NearestPriorArt { get; set; }
    public List<string> PriorArtCitationIds { get; set; } = new();
    public double? Score { get; set; }
    public Dictionary<string, double> ScoreBreakdown { get; set; } = new();
    public SafetyAssessment? Safety { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

// Materials Explorer models
public class MaterialCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FitRationale { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
    public List<string> CitationIds { get; set; } = new();
    public SafetyAssessment? Safety { get; set; }
    public string DiyFeasibility { get; set; } = string.Empty;
    public List<string> TestChecklist { get; set; } = new();
    public double FitScore { get; set; }
    public int Rank { get; set; }
}

public class MaterialsQuery
{
    public Dictionary<string, string> PropertyTargets { get; set; } = new();
    public Dictionary<string, string> Filters { get; set; } = new();
    public List<string> IncludeMaterials { get; set; } = new();
    public List<string> AvoidMaterials { get; set; } = new();
    public List<string> AvoidHazards { get; set; } = new();
}

// Programming Research + IP models
public class ApproachEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, string> Evaluation { get; set; } = new();
    public List<string> CitationIds { get; set; } = new();
    public IpAssessment? IpInfo { get; set; }
    public bool IsRecommended { get; set; }
    public string Rationale { get; set; } = string.Empty;
}

public class ProgrammingResearchResult
{
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public List<ApproachEntry> ApproachMatrix { get; set; } = new();
    public string RecommendedApproach { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public string ImplementationPlan { get; set; } = string.Empty;
    public List<IpAssessment> IpSummary { get; set; } = new();
    public List<string> DesignAroundOptions { get; set; } = new();
}

// Idea Fusion models
public enum FusionMode { Blend, CrossApply, Substitute, Optimize }

public class FusionRequest
{
    public string SessionId { get; set; } = string.Empty;
    public List<string> InputSourceIds { get; set; } = new();  // reports/notes/sessions
    public FusionMode Mode { get; set; } = FusionMode.Blend;
    public string Prompt { get; set; } = string.Empty;
}

public class FusionResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public FusionMode Mode { get; set; }
    public string Proposal { get; set; } = string.Empty;
    public Dictionary<string, string> ProvenanceMap { get; set; } = new(); // claim -> source
    public List<string> CitationIds { get; set; } = new();
    public SafetyAssessment? SafetyNotes { get; set; }
    public IpAssessment? IpNotes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

// Notebook model
public class NotebookEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = new();
}

// ---- Repo Intelligence & Project Fusion models ----

public class RepoProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PrimaryLanguage { get; set; } = string.Empty;
    public List<string> Languages { get; set; } = new();
    public List<string> Frameworks { get; set; } = new();
    public List<RepoDependency> Dependencies { get; set; } = new();
    public int Stars { get; set; }
    public int Forks { get; set; }
    public int OpenIssues { get; set; }
    public List<string> Topics { get; set; } = new();
    public DateTime? LastCommitUtc { get; set; }
    public string ReadmeContent { get; set; } = string.Empty;
    public List<string> Strengths { get; set; } = new();
    public List<string> Gaps { get; set; } = new();
    public List<ComplementProject> ComplementSuggestions { get; set; } = new();
    /// <summary>First few files/folders from the repo root — proof of live scan.</summary>
    public List<RepoEntry> TopLevelEntries { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Repo RAG fields
    /// <summary>LLM-generated architecture summary of the codebase.</summary>
    public string CodeBook { get; set; } = string.Empty;
    /// <summary>Git HEAD SHA at last index — used for cache invalidation.</summary>
    public string TreeSha { get; set; } = string.Empty;
    /// <summary>Number of source files indexed into chunks.</summary>
    public int IndexedFileCount { get; set; }
    /// <summary>Total chunks created from source files.</summary>
    public int IndexedChunkCount { get; set; }
    /// <summary>Full contents of manifest files (package.json, .csproj, etc.) — not persisted, used during analysis.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> ManifestContents { get; set; } = new();

    /// <summary>Which LLM model generated the analysis (strengths, gaps, CodeBook).</summary>
    public string? AnalysisModelUsed { get; set; }

    /// <summary>Pipeline telemetry: LLM call count, phase durations, total time.</summary>
    public ScanTelemetry? Telemetry { get; set; }

    /// <summary>Deterministic fact sheet built from code analysis before LLM runs. Null until Phase 2.5.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RepoFactSheet? FactSheet { get; set; }
}

// ─── Deterministic Fact Sheet (zero-LLM ground truth) ───

/// <summary>
/// Deterministic, code-analysis-derived ground truth about a repository.
/// Built from manifest parsing, grep patterns, file tree walking, and import cross-referencing.
/// Injected into LLM prompts so the model summarizes proven facts instead of guessing.
/// </summary>
public class RepoFactSheet
{
    /// <summary>Packages that are both in the manifest AND actively used in source code (import/using found).</summary>
    public List<PackageEvidence> ActivePackages { get; set; } = new();

    /// <summary>Packages in the manifest but with no matching import/using in any source file.</summary>
    public List<PackageEvidence> PhantomPackages { get; set; } = new();

    /// <summary>Capabilities proven to exist by regex/grep patterns in source code.</summary>
    public List<CapabilityFingerprint> ProvenCapabilities { get; set; } = new();

    /// <summary>Capabilities confirmed ABSENT after scanning the full codebase.</summary>
    public List<CapabilityFingerprint> ConfirmedAbsent { get; set; } = new();

    /// <summary>Diagnostic files/directories that exist (e.g., .github/workflows, Dockerfile).</summary>
    public List<string> DiagnosticFilesPresent { get; set; } = new();

    /// <summary>Diagnostic files/directories confirmed missing.</summary>
    public List<string> DiagnosticFilesMissing { get; set; } = new();

    /// <summary>Exact test count ([Fact] + [Theory] attributes, or equivalent for other ecosystems).</summary>
    public int TestMethodCount { get; set; }

    /// <summary>Number of test files found.</summary>
    public int TestFileCount { get; set; }

    /// <summary>Total source files scanned for analysis.</summary>
    public int TotalSourceFiles { get; set; }

    /// <summary>App type determined from project structure (e.g., "WPF desktop", "ASP.NET Core web API").</summary>
    public string AppType { get; set; } = string.Empty;

    /// <summary>Database technology determined from code patterns (e.g., "Raw SQLite via Microsoft.Data.Sqlite").</summary>
    public string DatabaseTechnology { get; set; } = string.Empty;

    /// <summary>Test framework determined from packages (e.g., "xUnit", "NUnit", "Jest").</summary>
    public string TestFramework { get; set; } = string.Empty;

    /// <summary>Primary language ecosystem for complement validation.</summary>
    public string Ecosystem { get; set; } = string.Empty;

    /// <summary>Render the fact sheet as a structured prompt section for LLM injection.</summary>
    public string ToPromptSection()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════");
        sb.AppendLine("  VERIFIED GROUND TRUTH — DO NOT CONTRADICT  ");
        sb.AppendLine("══════════════════════════════════════════════");
        sb.AppendLine();

        if (ActivePackages.Count > 0)
        {
            sb.AppendLine("PACKAGES (installed AND actively used in code):");
            foreach (var p in ActivePackages)
                sb.AppendLine($"  ✓ {p.PackageName} {p.Version} — {p.Evidence}");
            sb.AppendLine();
        }

        if (PhantomPackages.Count > 0)
        {
            sb.AppendLine("PACKAGES (installed but UNUSED — no matching imports found in source):");
            foreach (var p in PhantomPackages)
                sb.AppendLine($"  ⚠ {p.PackageName} {p.Version} — zero usage detected");
            sb.AppendLine();
        }

        if (ProvenCapabilities.Count > 0)
        {
            sb.AppendLine("CAPABILITIES PROVEN BY CODE PATTERNS:");
            foreach (var c in ProvenCapabilities)
                sb.AppendLine($"  ✓ {c.Capability}: {c.Evidence}");
            sb.AppendLine();
        }

        if (ConfirmedAbsent.Count > 0)
        {
            sb.AppendLine("CONFIRMED ABSENT (scanned full codebase, not found):");
            foreach (var c in ConfirmedAbsent)
                sb.AppendLine($"  ✗ {c.Capability}: {c.Evidence}");
            sb.AppendLine();
        }

        if (DiagnosticFilesPresent.Count > 0)
            sb.AppendLine($"FILES/DIRS PRESENT: {string.Join(", ", DiagnosticFilesPresent)}");
        if (DiagnosticFilesMissing.Count > 0)
            sb.AppendLine($"FILES/DIRS MISSING: {string.Join(", ", DiagnosticFilesMissing)}");

        if (!string.IsNullOrEmpty(AppType))
            sb.AppendLine($"APP TYPE: {AppType}");
        if (!string.IsNullOrEmpty(DatabaseTechnology))
            sb.AppendLine($"DATABASE: {DatabaseTechnology}");
        if (!string.IsNullOrEmpty(TestFramework))
            sb.AppendLine($"TEST FRAMEWORK: {TestFramework} ({TestMethodCount} test methods in {TestFileCount} files)");
        if (!string.IsNullOrEmpty(Ecosystem))
            sb.AppendLine($"ECOSYSTEM: {Ecosystem}");

        sb.AppendLine();
        sb.AppendLine("RULES FOR THE LLM:");
        sb.AppendLine("- Do NOT list phantom packages as active frameworks.");
        sb.AppendLine("- Do NOT claim a gap for any capability listed under PROVEN.");
        sb.AppendLine("- Do NOT claim a strength for any capability listed under ABSENT.");
        sb.AppendLine("- Strengths and gaps MUST be consistent with this fact sheet.");
        sb.AppendLine("- Do NOT embellish or add details beyond what this fact sheet states.");
        sb.AppendLine("  Example: if the fact sheet says 'Circuit breaker' do NOT add 'with Polly' unless Polly is listed.");
        sb.AppendLine("  Example: if 'Authentication / auth system' is NOT listed under PROVEN, do NOT claim it.");
        sb.AppendLine("- For each strength, cite the SPECIFIC class/service name from the code (not from this fact sheet).");
        sb.AppendLine("══════════════════════════════════════════════");
        return sb.ToString();
    }
}

/// <summary>Evidence that a package is installed and (optionally) used.</summary>
public class PackageEvidence
{
    public string PackageName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    /// <summary>How usage was detected (e.g., "using UglyToad.PdfPig found in PdfIngestionService.cs").</summary>
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>A capability fingerprint detected (or confirmed absent) via code pattern scanning.</summary>
public class CapabilityFingerprint
{
    /// <summary>Human-readable name (e.g., "Circuit breaker", "RAG/vector search").</summary>
    public string Capability { get; set; } = string.Empty;
    /// <summary>Evidence string (e.g., "LlmCircuitBreaker.cs — Closed/Open/HalfOpen state machine").</summary>
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>
/// Captures pipeline performance telemetry for a repo scan, including
/// LLM call count/duration, phase timings, and retrieval/search statistics.
/// </summary>
public class ScanTelemetry
{
    /// <summary>Total wall-clock time for the entire scan.</summary>
    public long TotalDurationMs { get; set; }

    /// <summary>Individual LLM call records.</summary>
    public List<LlmCallRecord> LlmCalls { get; set; } = new();

    /// <summary>Phase-level timing breakdown.</summary>
    public List<PhaseTimingRecord> Phases { get; set; } = new();

    /// <summary>Total number of LLM calls made during this scan.</summary>
    public int LlmCallCount => LlmCalls.Count;

    /// <summary>Total LLM latency in milliseconds.</summary>
    public long TotalLlmDurationMs => LlmCalls.Sum(c => c.DurationMs);

    /// <summary>Total RAG retrieval calls (HybridSearchAsync).</summary>
    public int RetrievalCallCount { get; set; }

    /// <summary>Total web search calls.</summary>
    public int WebSearchCallCount { get; set; }

    /// <summary>Total GitHub API calls (metadata + enrichment).</summary>
    public int GitHubApiCallCount { get; set; }

    /// <summary>Concise summary for display.</summary>
    public string Summary =>
        $"{LlmCallCount} LLM calls ({TotalLlmDurationMs:N0}ms) | " +
        $"{RetrievalCallCount} RAG queries | " +
        $"{WebSearchCallCount} web searches | " +
        $"{GitHubApiCallCount} GitHub API calls | " +
        $"Total: {TotalDurationMs / 1000.0:F1}s";
}

/// <summary>Records a single LLM call with timing and metadata.</summary>
public class LlmCallRecord
{
    /// <summary>Human-readable label (e.g. "CodeBook Generation", "RAG Analysis").</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Which model handled this call.</summary>
    public string? Model { get; set; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Whether the response was truncated.</summary>
    public bool WasTruncated { get; set; }

    /// <summary>Approximate prompt length (characters).</summary>
    public int PromptLength { get; set; }

    /// <summary>Approximate response length (characters).</summary>
    public int ResponseLength { get; set; }
}

/// <summary>Records timing for one pipeline phase.</summary>
public class PhaseTimingRecord
{
    public string Phase { get; set; } = string.Empty;
    public long DurationMs { get; set; }
}

/// <summary>A single file or directory entry in a repo, used as scan-proof.</summary>
public class RepoEntry
{
    public string Name { get; set; } = string.Empty;
    /// <summary>"file" or "dir".</summary>
    public string Type { get; set; } = "file";
    public string DisplayIcon => Type == "dir" ? "📁" : "📄";
}

public class RepoDependency
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string ManifestFile { get; set; } = string.Empty;
}

public class ComplementProject
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string WhatItAdds { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Maturity { get; set; } = string.Empty;
}

public enum ProjectFusionGoal { Merge, Extend, Compare, Architect }

public enum FusionInputType { RepoProfile, FusionArtifact }

/// <summary>Scope for global memory queries.</summary>
public enum MemoryScope
{
    /// <summary>Search only within the current session.</summary>
    ThisSession,
    /// <summary>Search only chunks associated with a specific repository.</summary>
    ThisRepo,
    /// <summary>Search only chunks from a specific domain pack.</summary>
    ThisDomain,
    /// <summary>Search everything — the full hive mind.</summary>
    HiveMind
}

public class ProjectFusionInput
{
    public string Id { get; set; } = string.Empty;
    public FusionInputType Type { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class ProjectFusionRequest
{
    public string SessionId { get; set; } = string.Empty;
    public List<ProjectFusionInput> Inputs { get; set; } = new();
    public ProjectFusionGoal Goal { get; set; } = ProjectFusionGoal.Merge;
    public string FocusPrompt { get; set; } = string.Empty;
}

public class ProjectFusionArtifact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string InputSummary { get; set; } = string.Empty;
    public List<ProjectFusionInput> Inputs { get; set; } = new();
    public ProjectFusionGoal Goal { get; set; }
    public string UnifiedVision { get; set; } = string.Empty;
    public string ArchitectureProposal { get; set; } = string.Empty;
    public string TechStackDecisions { get; set; } = string.Empty;
    public Dictionary<string, string> FeatureMatrix { get; set; } = new(); // feature -> source
    public List<string> GapsClosed { get; set; } = new();
    public List<string> NewGaps { get; set; } = new();
    public IpAssessment? IpNotes { get; set; }
    public Dictionary<string, string> ProvenanceMap { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
