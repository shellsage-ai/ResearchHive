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
