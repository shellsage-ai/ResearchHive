namespace ResearchHive.Core.Models;

public enum JobType
{
    Research,
    Discovery,
    Materials,
    ProgrammingIP,
    Fusion
}

public enum JobState
{
    Pending,
    Planning,
    Searching,
    Acquiring,
    Extracting,
    Evaluating,
    Drafting,
    Validating,
    Reporting,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Real-time progress data emitted by ResearchJobRunner at each state transition.
/// </summary>
public class JobProgressEventArgs : EventArgs
{
    public string JobId { get; set; } = "";
    public JobState State { get; set; }
    public string StepDescription { get; set; } = "";
    public int SourcesFound { get; set; }
    public int SourcesFailed { get; set; }
    public int SourcesBlocked { get; set; }
    public int TargetSources { get; set; }
    public double CoverageScore { get; set; }
    public int CurrentIteration { get; set; }
    public int MaxIterations { get; set; }
    public List<SourceHealthEntry> SourceHealth { get; set; } = new();
    public string? LogMessage { get; set; }

    // C1: Enhanced progress reporting
    public int SubQuestionsTotal { get; set; }
    public int SubQuestionsAnswered { get; set; }
    public double GroundingScore { get; set; }
    public double SufficiencyScore { get; set; }
    public int BrowserPoolAvailable { get; set; }
    public int BrowserPoolTotal { get; set; }
}

public class SourceHealthEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public SourceFetchStatus Status { get; set; }
    public int HttpStatus { get; set; }
    public string? Reason { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public enum SourceFetchStatus
{
    Success,
    Blocked,
    Timeout,
    Paywall,
    Error,
    CircuitBroken
}

public class ResearchJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public JobType Type { get; set; } = JobType.Research;
    public JobState State { get; set; } = JobState.Pending;
    public string Prompt { get; set; } = string.Empty;
    public string? Plan { get; set; }
    public List<string> SearchQueries { get; set; } = new();
    public List<string> SearchLanes { get; set; } = new();
    public List<string> AcquiredSourceIds { get; set; } = new();
    public List<string> SubQuestions { get; set; } = new();
    public Dictionary<string, string> SubQuestionCoverage { get; set; } = new(); // subQ -> "answered"|"partial"|"unanswered"
    public int TargetSourceCount { get; set; } = 5;
    public int MaxIterations { get; set; } = 3;
    public int CurrentIteration { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public List<JobStep> Steps { get; set; } = new();
    public string? CheckpointData { get; set; }

    // Outputs
    public string? MostSupportedView { get; set; }
    public string? CredibleAlternatives { get; set; }
    public string? ExecutiveSummary { get; set; }
    public string? FullReport { get; set; }
    public string? ActivityReport { get; set; }
    public double GroundingScore { get; set; }
    public List<ReplayEntry> ReplayEntries { get; set; } = new();
}

public class JobStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string JobId { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public JobState StateAfter { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; } = true;
    public string? Error { get; set; }
}

public class ReplayEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EntryType { get; set; } = string.Empty; // "search", "snapshot", "extract", "evaluate", "draft", etc.
    public string? LinkedSourceId { get; set; }
    public string? LinkedCitationId { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Data { get; set; } = new();
}

public class ClaimLedger
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string JobId { get; set; } = string.Empty;
    public string Claim { get; set; } = string.Empty;
    public string Support { get; set; } = string.Empty; // "cited", "hypothesis", "assumption"
    public List<string> CitationIds { get; set; } = new();
    public string? Explanation { get; set; }
}

public class Report
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty; // "executive", "full", "activity", "replay"
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Format { get; set; } = "markdown";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>
/// A follow-up Q&amp;A exchange within a research session.
/// </summary>
public class QaMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    /// <summary>"session" or a Report.Id for scoped Q&amp;A.</summary>
    public string Scope { get; set; } = "session";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A pinned evidence item persisted across session reloads.
/// </summary>
public class PinnedEvidence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; set; } = string.Empty;
    public string ChunkId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float Score { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime PinnedUtc { get; set; } = DateTime.UtcNow;
}
