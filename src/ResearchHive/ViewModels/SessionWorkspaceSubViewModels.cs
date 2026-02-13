using CommunityToolkit.Mvvm.ComponentModel;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.ViewModels;

public class GlobalChunkViewModel
{
    public GlobalChunk Chunk { get; }
    public string Id => Chunk.Id;
    public string SourceType => Chunk.SourceType;
    public string DomainPack => Chunk.DomainPack ?? "";
    public string SessionId => Chunk.SessionId ?? "";
    public string TextPreview => Chunk.Text.Length > 200 ? Chunk.Text[..200] + "..." : Chunk.Text;
    public string FullText => Chunk.Text;
    public string Tags => Chunk.Tags.Count > 0 ? string.Join(", ", Chunk.Tags) : "";
    public string Promoted => Chunk.PromotedUtc.ToLocalTime().ToString("g");
    public string TypeIcon => Chunk.SourceType switch
    {
        "report" => "📊",
        "strategy" => "🧠",
        "repo_code" => "💻",
        "repo_doc" => "📄",
        _ => "📝"
    };
    public GlobalChunkViewModel(GlobalChunk chunk) => Chunk = chunk;
}

// ---- Sub ViewModels ----
public class JobViewModel
{
    public ResearchJob Job { get; }
    public string Title => $"{Job.Type}: {Job.Prompt[..Math.Min(50, Job.Prompt.Length)]}";
    public string State => Job.State.ToString();
    public string Created => Job.CreatedUtc.ToLocalTime().ToString("g");
    public string Sources => $"{Job.AcquiredSourceIds.Count}/{Job.TargetSourceCount}";
    public string StateColor => Job.State switch
    {
        JobState.Completed => "#4CAF50",
        JobState.Failed => "#F44336",
        JobState.Paused => "#FF9800",
        _ => "#2196F3"
    };
    public JobViewModel(ResearchJob job) => Job = job;
}

public class SnapshotViewModel
{
    public Snapshot Snapshot { get; }
    public string Title => Snapshot.Title;
    public string Url => Snapshot.Url;
    public string Captured => Snapshot.CapturedUtc.ToLocalTime().ToString("g");
    public bool IsBlocked => Snapshot.IsBlocked;
    public string Status => Snapshot.IsBlocked ? $"Blocked: {Snapshot.BlockReason}" : $"OK ({Snapshot.HttpStatus})";
    public SnapshotViewModel(Snapshot snapshot) => Snapshot = snapshot;
}

public partial class NotebookEntryViewModel : ObservableObject
{
    public NotebookEntry Entry { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isEditing;
    public string Updated => Entry.UpdatedUtc.ToLocalTime().ToString("g");

    public NotebookEntryViewModel(NotebookEntry entry)
    {
        Entry = entry;
        _title = entry.Title;
        _content = entry.Content;
    }

    partial void OnTitleChanged(string value)
    {
        Entry.Title = value;
    }

    partial void OnContentChanged(string value)
    {
        Entry.Content = value;
    }
}

public class EvidenceItemViewModel
{
    public string SourceId { get; set; } = "";
    public string SourceType { get; set; } = "";
    public float Score { get; set; }
    public string Text { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string ScoreDisplay => $"{Score:F2}";
    public bool HasSourceUrl => !string.IsNullOrEmpty(SourceUrl);
}

public class ReportViewModel
{
    public Report Report { get; }
    public string Title => Report.Title;
    public string Type => Report.ReportType;
    public string Created => Report.CreatedUtc.ToLocalTime().ToString("g");
    public ReportViewModel(Report report) => Report = report;
}

public class ReplayEntryViewModel
{
    public ReplayEntry Entry { get; }
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public string Type => Entry.EntryType;
    public int Order => Entry.Order;
    public string Timestamp => Entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public ReplayEntryViewModel(ReplayEntry entry) => Entry = entry;
}

public class IdeaCardViewModel
{
    public IdeaCard Card { get; }
    public string Title => Card.Title;
    public string Hypothesis => Card.Hypothesis;
    public string Mechanism => Card.Mechanism;
    public string TestPlan => Card.MinimalTestPlan;
    public string Falsification => Card.Falsification;
    public string Risks => string.Join(", ", Card.Risks);
    public string NoveltyCheck => Card.NoveltyCheck ?? "Not checked";
    public string Score => Card.Score?.ToString("F2") ?? "N/A";
    public string ScoreBreakdown => string.Join("\n", Card.ScoreBreakdown.Select(kv => $"{kv.Key}: {kv.Value:F1}"));
    public IdeaCardViewModel(IdeaCard card) => Card = card;
}

public class MaterialCandidateViewModel
{
    public MaterialCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public string Category => Candidate.Category;
    public string FitRationale => Candidate.FitRationale;
    public string FitScore => Candidate.FitScore.ToString("F2");
    public int Rank => Candidate.Rank;
    public string Safety => Candidate.Safety?.Level.ToString() ?? "Unknown";
    public string Environment => Candidate.Safety?.RecommendedEnvironment ?? "N/A";
    public string PPE => string.Join(", ", Candidate.Safety?.MinimumPPE ?? new());
    public string Hazards => string.Join(", ", Candidate.Safety?.Hazards ?? new());
    public string Properties => string.Join("\n", Candidate.Properties.Select(kv => $"{kv.Key}: {kv.Value}"));
    public string TestChecklist => string.Join("\n", Candidate.TestChecklist.Select(t => $"☐ {t}"));
    public MaterialCandidateViewModel(MaterialCandidate candidate) => Candidate = candidate;
}

public class ApproachViewModel
{
    public ApproachEntry Approach { get; }
    public string Name => Approach.Name;
    public string Description => Approach.Description;
    public bool IsRecommended => Approach.IsRecommended;
    public string Evaluation => string.Join("\n", Approach.Evaluation.Select(kv => $"{kv.Key}: {kv.Value}"));
    public string IpInfo => Approach.IpInfo != null
        ? $"License: {Approach.IpInfo.LicenseSignal}\nRisks: {string.Join(", ", Approach.IpInfo.RiskFlags)}"
        : "No IP data";
    public ApproachViewModel(ApproachEntry approach) => Approach = approach;
}

public class ArtifactViewModel
{
    public Artifact Artifact { get; }
    public string Name => Artifact.OriginalName;
    public string Type => Artifact.ContentType;
    public string Size => $"{Artifact.SizeBytes / 1024.0:F1} KB";
    public string Ingested => Artifact.IngestedUtc.ToLocalTime().ToString("g");
    public ArtifactViewModel(Artifact artifact) => Artifact = artifact;
}

public class CaptureViewModel
{
    public Capture Capture { get; }
    public string Description => Capture.SourceDescription;
    public string OcrText => Capture.OcrText ?? "No text extracted";
    public int BoxCount => Capture.Boxes.Count;
    public string Captured => Capture.CapturedUtc.ToLocalTime().ToString("g");
    public CaptureViewModel(Capture capture) => Capture = capture;
}

public class FusionResultViewModel
{
    public FusionResult Result { get; }
    public string Mode => Result.Mode.ToString();
    public string Proposal => Result.Proposal;
    public string ProvenanceMap => string.Join("\n", Result.ProvenanceMap.Select(kv => $"• {kv.Key} ← {kv.Value}"));
    public string Created => Result.CreatedUtc.ToLocalTime().ToString("g");
    public bool HasSafetyNotes => Result.SafetyNotes != null;
    public bool HasIpNotes => Result.IpNotes != null;
    public string SafetyNotes => Result.SafetyNotes is { } s
        ? $"⚠ {s.Level} — {string.Join(", ", s.Hazards)}" + (string.IsNullOrEmpty(s.Notes) ? "" : $"\n{s.Notes}")
        : "";
    public string IpNotes => Result.IpNotes is { } ip
        ? $"License: {ip.LicenseSignal} ({ip.UncertaintyLevel})" + (ip.RiskFlags.Count > 0 ? $" — {string.Join(", ", ip.RiskFlags)}" : "") + (string.IsNullOrEmpty(ip.Notes) ? "" : $"\n{ip.Notes}")
        : "";
    public FusionResultViewModel(FusionResult result) => Result = result;
}

public class FusionPromptTemplate
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Prompt { get; init; } = "";
    public FusionMode Mode { get; init; } = FusionMode.Blend;
    public string DisplayName => $"{ModeIcon} {Name}";
    private string ModeIcon => Mode switch
    {
        FusionMode.Blend => "🔀",
        FusionMode.CrossApply => "🔄",
        FusionMode.Substitute => "♻️",
        FusionMode.Optimize => "⚡",
        _ => "🔗"
    };
    public override string ToString() => DisplayName;

    public static readonly FusionPromptTemplate[] BuiltIn = new[]
    {
        new FusionPromptTemplate
        {
            Name = "Custom Prompt",
            Description = "Write your own fusion prompt from scratch",
            Prompt = "",
            Mode = FusionMode.Blend
        },
        new FusionPromptTemplate
        {
            Name = "Unified Theory",
            Description = "Merge all key findings into one cohesive, unified proposal",
            Prompt = "Synthesize the key findings from all evidence in this session into a single unified proposal. Identify the common threads, resolve any tensions between sources, and produce a cohesive narrative that captures the best insights from each piece of evidence.",
            Mode = FusionMode.Blend
        },
        new FusionPromptTemplate
        {
            Name = "Best-of-Breed Hybrid",
            Description = "Cherry-pick the strongest element from each source and merge them",
            Prompt = "Identify the single strongest contribution from each major source in this session. Combine these best-of-breed elements into a hybrid approach that leverages each source's unique advantage. Explain why each element was selected and how they fit together.",
            Mode = FusionMode.Blend
        },
        new FusionPromptTemplate
        {
            Name = "Cross-Domain Transfer",
            Description = "Apply a technique from one field to a problem in another",
            Prompt = "Take the most novel technique or methodology found in this research and cross-apply it to a different domain or problem space. Describe specifically how the technique would need to be adapted, what new opportunities it creates, and what risks or limitations arise in the new context.",
            Mode = FusionMode.CrossApply
        },
        new FusionPromptTemplate
        {
            Name = "Analogy Bridge",
            Description = "Find a structural analogy between two findings and build on it",
            Prompt = "Identify a deep structural analogy between two different findings or approaches in this session's evidence. Use that analogy as a bridge to propose a novel solution that neither source alone would suggest. Explain the analogy clearly and why it holds.",
            Mode = FusionMode.CrossApply
        },
        new FusionPromptTemplate
        {
            Name = "Safer Alternative",
            Description = "Replace risky or expensive components with safer substitutes",
            Prompt = "Review the approaches and materials discussed in this session's evidence. For any component that is expensive, hazardous, patent-encumbered, or hard to source, propose a safer or more accessible substitute. Preserve the core functionality while reducing risk, cost, or complexity.",
            Mode = FusionMode.Substitute
        },
        new FusionPromptTemplate
        {
            Name = "Open-Source Swap",
            Description = "Replace proprietary dependencies with open-source alternatives",
            Prompt = "Identify any proprietary, patented, or restrictively licensed elements in the approaches discussed in this session. For each, propose an open-source or freely available substitute. Compare the tradeoffs in performance, maturity, and community support.",
            Mode = FusionMode.Substitute
        },
        new FusionPromptTemplate
        {
            Name = "Performance Optimizer",
            Description = "Refine for maximum efficiency based on benchmarks and data",
            Prompt = "Based on all performance data, benchmarks, and quantitative findings in this session's evidence, propose an optimized configuration or approach that maximizes throughput, minimizes latency, or reduces resource consumption. Back every recommendation with specific data from the evidence.",
            Mode = FusionMode.Optimize
        },
        new FusionPromptTemplate
        {
            Name = "Simplify & Streamline",
            Description = "Remove unnecessary complexity while keeping core value",
            Prompt = "Analyze the approaches in this session and identify unnecessary complexity — redundant steps, over-engineering, or features that add cost without proportional value. Propose a streamlined version that preserves the core benefits with fewer moving parts. Justify each simplification.",
            Mode = FusionMode.Optimize
        },
        new FusionPromptTemplate
        {
            Name = "Contradiction Resolver",
            Description = "Reconcile conflicting claims into a nuanced position",
            Prompt = "Identify the most significant contradictions or tensions between sources in this session. For each conflict, analyze why the sources disagree (different methodology, scope, context) and propose a reconciled position that accounts for when each perspective applies. Produce a nuanced synthesis rather than simply picking a winner.",
            Mode = FusionMode.Blend
        },
    };
}

public class SourceHealthViewModel
{
    public SourceHealthEntry Entry { get; }
    public string Url => Entry.Url;
    public string Title => Entry.Title;
    public string Status => Entry.Status.ToString();
    public int HttpStatus => Entry.HttpStatus;
    public string Reason => Entry.Reason ?? "";
    public string Timestamp => Entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string StatusIcon => Entry.Status switch
    {
        SourceFetchStatus.Success => "✅",
        SourceFetchStatus.Blocked => "🚫",
        SourceFetchStatus.Timeout => "⏱",
        SourceFetchStatus.Paywall => "💰",
        SourceFetchStatus.CircuitBroken => "⚡",
        _ => "❌"
    };
    public string StatusColor => Entry.Status switch
    {
        SourceFetchStatus.Success => "#4CAF50",
        SourceFetchStatus.Blocked => "#F44336",
        SourceFetchStatus.Timeout => "#FF9800",
        SourceFetchStatus.Paywall => "#FF9800",
        SourceFetchStatus.CircuitBroken => "#F44336",
        _ => "#F44336"
    };
    public SourceHealthViewModel(SourceHealthEntry entry) => Entry = entry;
}

public class SearchEngineHealthViewModel
{
    public SearchEngineHealthEntry Entry { get; }
    public string EngineName => Entry.EngineName;
    public string StatusIcon => Entry.StatusIcon;
    public string StatusDisplay => Entry.StatusDisplay;
    public string RateDisplay => Entry.QueriesAttempted > 0
        ? $"{Entry.QueriesSucceeded}/{Entry.QueriesAttempted}"
        : "—";
    public int TotalResults => Entry.TotalResultsFound;
    public string StatusColor => Entry.StatusDisplay switch
    {
        "Healthy" => "#4CAF50",
        "Degraded" => "#FF9800",
        "Failed" => "#F44336",
        "Skipped" => "#9E9E9E",
        _ => "#607D8B"
    };
    public SearchEngineHealthViewModel(SearchEngineHealthEntry entry) => Entry = entry;
}

/// <summary>
/// Represents a single tab in the workspace navigation.
/// Filtered per domain pack so irrelevant tabs are hidden.
/// </summary>
public class TabItemViewModel
{
    public string Label { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Group { get; init; } = "";
    public TabItemViewModel(string emoji, string name, string tag, string group = "Core")
    {
        Label = $"{emoji} {name}";
        Tag = tag;
        Group = group;
    }
}

/// <summary>
/// Q&A message view model for the follow-up chat tab.
/// </summary>
public partial class QaMessageViewModel : ObservableObject
{
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private string? _modelUsed;
}

/// <summary>
/// Represents a scope option for Q&A queries (session-wide or specific report).
/// </summary>
public class QaScopeOption
{
    public string Value { get; }
    public string DisplayName { get; }
    public QaScopeOption(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }
    public override string ToString() => DisplayName;
}

// ---- New Feature Sub ViewModels ----

public class CrossSessionResultViewModel
{
    public CrossSessionResult Result { get; }
    public string SessionTitle => Result.SessionTitle;
    public string DomainPack => Result.DomainPack.ToString();
    public string SourceUrl => Result.SourceUrl;
    public string ChunkText => Result.Chunk.Text.Length > 300
        ? Result.Chunk.Text[..300] + "..."
        : Result.Chunk.Text;
    public string SessionId => Result.SessionId;
    public CrossSessionResultViewModel(CrossSessionResult result) => Result = result;
}

public class CrossSessionReportResultViewModel
{
    public CrossSessionReportResult Result { get; }
    public string SessionTitle => Result.SessionTitle;
    public string ReportTitle => Result.ReportTitle;
    public string ReportType => Result.ReportType;
    public string Snippet => Result.Snippet.Length > 300 ? Result.Snippet[..300] + "..." : Result.Snippet;
    public string Created => Result.CreatedUtc.ToLocalTime().ToString("g");
    public string SessionId => Result.SessionId;
    public CrossSessionReportResultViewModel(CrossSessionReportResult result) => Result = result;
}

public class CitationVerificationViewModel
{
    public CitationVerification V { get; }
    public string Claim => V.ClaimText.Length > 200 ? V.ClaimText[..200] + "..." : V.ClaimText;
    public string CitationLabel => V.CitationLabel;
    public string Status => V.Status.ToString();
    public string StatusIcon => V.Status switch
    {
        VerificationStatus.Verified => "✅",
        VerificationStatus.Plausible => "🟡",
        VerificationStatus.Unverified => "❌",
        VerificationStatus.NoSource => "❓",
        _ => "?"
    };
    public string OverlapDisplay => $"{V.Confidence:P0}";
    public CitationVerificationViewModel(CitationVerification v) => V = v;
}

public class ContradictionViewModel
{
    public Contradiction C { get; }
    public string TextA => C.ChunkA.Text.Length > 200 ? C.ChunkA.Text[..200] + "..." : C.ChunkA.Text;
    public string TextB => C.ChunkB.Text.Length > 200 ? C.ChunkB.Text[..200] + "..." : C.ChunkB.Text;
    public string SourceA => C.ChunkA.SourceId;
    public string SourceB => C.ChunkB.SourceId;
    public string Type => C.Type.ToString();
    public string Score => $"{C.ContradictionScore:F2}";
    public string TypeIcon => C.Type switch
    {
        ContradictionType.DirectContradiction => "⚡",
        ContradictionType.NumericDisagreement => "🔢",
        ContradictionType.InterpretationDifference => "🔄",
        _ => "?"
    };
    public ContradictionViewModel(Contradiction c) => C = c;
}

// ---- Repo Intelligence & Project Fusion ViewModels ----

public class RepoProfileViewModel
{
    public RepoProfile Profile { get; }
    public string Owner => Profile.Owner;
    public string RepoName => Profile.Name;
    public string FullName => $"{Profile.Owner}/{Profile.Name}";
    public string Description => Profile.Description.Length > 200 ? Profile.Description[..200] + "..." : Profile.Description;
    public string PrimaryLanguage => Profile.PrimaryLanguage;
    public string Stars => Profile.Stars.ToString("N0");
    public string Forks => Profile.Forks.ToString("N0");
    public int DependencyCount => Profile.Dependencies.Count;
    public string Strengths => string.Join("\n", Profile.Strengths.Select(s => $"✅ {s}"));
    public string Gaps => string.Join("\n", Profile.Gaps.Select(g => $"🔸 {g}"));
    public int ComplementCount => Profile.ComplementSuggestions.Count;
    public string Complements => string.Join("\n", Profile.ComplementSuggestions.Select(c => $"• {c.Name}: {c.Purpose}"));
    public List<ComplementViewModel> ComplementItems => Profile.ComplementSuggestions
        .Select(c => new ComplementViewModel(c)).ToList();
    public string Topics => Profile.Topics.Count > 0 ? string.Join(", ", Profile.Topics) : "";
    public string Frameworks => string.Join(", ", Profile.Frameworks);
    public string Created => Profile.CreatedUtc.ToLocalTime().ToString("g");
    public string RepoUrl => Profile.RepoUrl;
    /// <summary>Which model generated this analysis.</summary>
    public string AnalysisModel => Profile.AnalysisModelUsed ?? "unknown";

    /// <summary>Full text of the entire profile for clipboard export.</summary>
    public string FullProfileText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Repository: {FullName}");
            sb.AppendLine($"URL: {Profile.RepoUrl}");
            sb.AppendLine($"Description: {Profile.Description}");
            sb.AppendLine($"Primary Language: {PrimaryLanguage}");
            sb.AppendLine($"Frameworks: {Frameworks}");
            sb.AppendLine($"Stars: {Stars} | Forks: {Forks} | Dependencies: {DependencyCount}");
            sb.AppendLine($"Analysis Model: {AnalysisModel}");
            sb.AppendLine($"Last Push: {LastCommitAgo}");
            if (Profile.TopLevelEntries.Count > 0)
                sb.AppendLine($"Root: {TopEntriesProof}");
            sb.AppendLine();
            sb.AppendLine("Strengths:");
            foreach (var s in Profile.Strengths) sb.AppendLine($"  ✅ {s}");
            sb.AppendLine();
            sb.AppendLine("Gaps:");
            foreach (var g in Profile.Gaps) sb.AppendLine($"  🔸 {g}");
            if (Profile.ComplementSuggestions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Complementary Projects:");
                foreach (var c in Profile.ComplementSuggestions)
                    sb.AppendLine($"  • {c.Name} ({c.Url}) — {c.Purpose}: {c.WhatItAdds}");
            }
            return sb.ToString();
        }
    }

    /// <summary>Human-readable "X days/months ago" from LastCommitUtc.</summary>
    public string LastCommitAgo
    {
        get
        {
            if (Profile.LastCommitUtc == null) return "unknown";
            var span = DateTime.UtcNow - Profile.LastCommitUtc.Value;
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{span.TotalDays / 365:F1}y ago";
        }
    }

    /// <summary>First 3 root entries as "\ud83d\udcc1 src, \ud83d\udcc4 README.md, \ud83d\udcc4 package.json"</summary>
    public string TopEntriesProof => Profile.TopLevelEntries.Count > 0
        ? string.Join(",  ", Profile.TopLevelEntries.Select(e => $"{e.DisplayIcon} {e.Name}"))
        : "";

    /// <summary>Full proof verification line shown in the UI.</summary>
    public string ProofBanner => Profile.TopLevelEntries.Count > 0
        ? $"\u2705 Verified via GitHub API  \u2502  Root: {TopEntriesProof}  \u2502  Last push: {LastCommitAgo}"
        : $"\u2705 Verified via GitHub API  \u2502  Last push: {LastCommitAgo}";

    public bool HasProof => Profile.TopLevelEntries.Count > 0 || Profile.LastCommitUtc != null;

    public RepoProfileViewModel(RepoProfile profile) => Profile = profile;
}

public class ProjectFusionArtifactViewModel
{
    public ProjectFusionArtifact Artifact { get; }
    public string Title => Artifact.Title;
    public string Goal => Artifact.Goal.ToString();
    public string InputSummary => Artifact.InputSummary;
    public string Vision => Artifact.UnifiedVision.Length > 400 ? Artifact.UnifiedVision[..400] + "..." : Artifact.UnifiedVision;
    public string Architecture => Artifact.ArchitectureProposal.Length > 400 ? Artifact.ArchitectureProposal[..400] + "..." : Artifact.ArchitectureProposal;
    public string TechStack => Artifact.TechStackDecisions;
    public string FeatureMatrix => string.Join("\n", Artifact.FeatureMatrix.Select(kv => $"• {kv.Key} ← {kv.Value}"));
    public int FeatureCount => Artifact.FeatureMatrix.Count;
    public string GapsClosed => string.Join("\n", Artifact.GapsClosed.Select(g => $"✅ {g}"));
    public string NewGaps => string.Join("\n", Artifact.NewGaps.Select(g => $"🔸 {g}"));
    public bool HasIpNotes => Artifact.IpNotes != null;
    public string IpNotes => Artifact.IpNotes?.Notes ?? "";
    public string ProvenanceMap => string.Join("\n", Artifact.ProvenanceMap.Select(kv => $"• {kv.Key} ← {kv.Value}"));
    public string Created => Artifact.CreatedUtc.ToLocalTime().ToString("g");

    /// <summary>Full text of entire fusion artifact for clipboard export.</summary>
    public string FullFusionText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Project Fusion: {Title}");
            sb.AppendLine($"Goal: {Goal}");
            sb.AppendLine($"Inputs: {InputSummary}");
            sb.AppendLine();
            sb.AppendLine("=== Unified Vision ===");
            sb.AppendLine(Artifact.UnifiedVision);
            sb.AppendLine();
            sb.AppendLine("=== Architecture ===");
            sb.AppendLine(Artifact.ArchitectureProposal);
            sb.AppendLine();
            sb.AppendLine("=== Tech Stack ===");
            sb.AppendLine(Artifact.TechStackDecisions);
            sb.AppendLine();
            sb.AppendLine("=== Feature Matrix ===");
            sb.AppendLine(FeatureMatrix);
            sb.AppendLine();
            sb.AppendLine("=== Gaps Closed ===");
            sb.AppendLine(GapsClosed);
            sb.AppendLine();
            sb.AppendLine("=== New Gaps ===");
            sb.AppendLine(NewGaps);
            if (HasIpNotes) { sb.AppendLine(); sb.AppendLine($"IP Notes: {IpNotes}"); }
            sb.AppendLine();
            sb.AppendLine("=== Provenance ===");
            sb.AppendLine(ProvenanceMap);
            return sb.ToString();
        }
    }

    public ProjectFusionArtifactViewModel(ProjectFusionArtifact artifact) => Artifact = artifact;
}

public class ComplementViewModel
{
    public ComplementProject Complement { get; }
    public string Name => Complement.Name;
    public string Url => Complement.Url;
    public string Purpose => Complement.Purpose;
    public string WhatItAdds => Complement.WhatItAdds;
    public string Category => Complement.Category;
    public string License => Complement.License;
    public string Maturity => Complement.Maturity;
    public bool HasUrl => !string.IsNullOrEmpty(Url);
    public string DisplayText => $"{Name} — {Purpose}: {WhatItAdds}";
    public string CopyText => $"{Name} ({Url}) — {Purpose}: {WhatItAdds} [License: {License}, Maturity: {Maturity}]";
    public ComplementViewModel(ComplementProject c) => Complement = c;
}

public partial class FusionInputOption : ObservableObject
{
    public string Id { get; }
    public FusionInputType InputType { get; }
    public string Title { get; }
    [ObservableProperty] private bool _isSelected;
    public FusionInputOption(string id, FusionInputType type, string title) { Id = id; InputType = type; Title = title; }
}

public class RepoQaMessageViewModel
{
    public string RepoUrl { get; init; } = "";
    public string Question { get; init; } = "";
    public string Answer { get; init; } = "";
    public string RepoLabel => RepoUrl.Length > 50 ? "..." + RepoUrl[^45..] : RepoUrl;
}

public class ProjectFusionTemplate
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Prompt { get; init; } = "";
    public ProjectFusionGoal Goal { get; init; } = ProjectFusionGoal.Merge;
    public string DisplayName => $"{GoalIcon} {Name}";
    private string GoalIcon => Goal switch
    {
        ProjectFusionGoal.Merge => "🔀",
        ProjectFusionGoal.Extend => "🔌",
        ProjectFusionGoal.Compare => "📊",
        ProjectFusionGoal.Architect => "🏗️",
        _ => "🔗"
    };

    public static readonly ProjectFusionTemplate[] BuiltIn = new[]
    {
        new ProjectFusionTemplate
        {
            Name = "Full Merge",
            Description = "Combine all repos into one unified project with a single architecture.",
            Prompt = "Merge all repositories into a single cohesive project. Resolve duplicate functionality, unify the tech stack, and produce a clean architecture.",
            Goal = ProjectFusionGoal.Merge
        },
        new ProjectFusionTemplate
        {
            Name = "Plugin Architecture",
            Description = "Keep the first repo as core, integrate others as plugins/extensions.",
            Prompt = "Use the first project as the core platform. Design a plugin architecture where other projects integrate as extensions. Define clear extension points and APIs.",
            Goal = ProjectFusionGoal.Extend
        },
        new ProjectFusionTemplate
        {
            Name = "Best of Each",
            Description = "Cherry-pick the best features from each repo into a new design.",
            Prompt = "Design a new system that takes the best feature from each input project. Don't merge blindly — be selective and justify each choice.",
            Goal = ProjectFusionGoal.Architect
        },
        new ProjectFusionTemplate
        {
            Name = "Gap Filler",
            Description = "Use complementary repos to fill gaps in the primary project.",
            Prompt = "The first project has known gaps. Use the other projects to fill those gaps. Focus on what the primary project is missing and how others can contribute.",
            Goal = ProjectFusionGoal.Extend
        },
        new ProjectFusionTemplate
        {
            Name = "Side-by-Side Comparison",
            Description = "Detailed comparison: architecture, tech stack, strengths, weaknesses.",
            Prompt = "Compare these projects in detail. Analyze architecture patterns, tech stack choices, testing strategies, documentation quality, and community health. Recommend which is better for different use cases.",
            Goal = ProjectFusionGoal.Compare
        },
        new ProjectFusionTemplate
        {
            Name = "Ecosystem Blueprint",
            Description = "Design a microservices/ecosystem where each repo becomes a service.",
            Prompt = "Design a microservices ecosystem where each input project becomes a service or component. Define service boundaries, communication protocols, shared data models, and deployment strategy.",
            Goal = ProjectFusionGoal.Architect
        },
        new ProjectFusionTemplate
        {
            Name = "Custom",
            Description = "Write your own fusion prompt and goal.",
            Prompt = "",
            Goal = ProjectFusionGoal.Merge
        }
    };
}
