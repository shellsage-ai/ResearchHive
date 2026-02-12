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
