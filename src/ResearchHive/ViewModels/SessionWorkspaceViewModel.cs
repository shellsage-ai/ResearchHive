using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ResearchHive.ViewModels;

public partial class SessionWorkspaceViewModel : ObservableObject
{
    private readonly string _sessionId;
    private readonly SessionManager _sessionManager;
    private readonly ArtifactStore _artifactStore;
    private readonly SnapshotService _snapshotService;
    private readonly OcrService _ocrService;
    private readonly IndexService _indexService;
    private readonly RetrievalService _retrievalService;
    private readonly ResearchJobRunner _researchRunner;
    private readonly DiscoveryJobRunner _discoveryRunner;
    private readonly ProgrammingJobRunner _programmingRunner;
    private readonly MaterialsJobRunner _materialsRunner;
    private readonly FusionJobRunner _fusionRunner;
    private readonly ExportService _exportService;
    private readonly LlmService _llmService;
    private readonly AppSettings _appSettings;
    private readonly IDialogService _dialogService;
    private readonly CrossSessionSearchService _crossSearch;
    private readonly CitationVerificationService _citationVerifier;
    private readonly ContradictionDetector _contradictionDetector;
    private readonly ResearchComparisonService _comparisonService;
    private readonly RepoIntelligenceJobRunner _repoRunner;
    private readonly ProjectFusionEngine _projectFusionEngine;
    private readonly GlobalMemoryService _globalMemory;
    private readonly NotificationService _notificationService;
    private readonly GitHubDiscoveryService _discoveryService;
    private CancellationTokenSource? _jobCts;
    private readonly DispatcherTimer _autoSaveTimer;
    private bool _notebookDirty;

    [ObservableProperty] private Session _session;
    [ObservableProperty] private string _activeTab = "Overview";

    // Overview
    [ObservableProperty] private string _overviewText = "";
    [ObservableProperty] private int _statSources;
    [ObservableProperty] private int _statEvidence;
    [ObservableProperty] private int _statReports;
    [ObservableProperty] private int _statNotes;
    [ObservableProperty] private int _statJobs;
    [ObservableProperty] private int _statPins;
    [ObservableProperty] private string _statDomainPack = "";
    [ObservableProperty] private string _statStatus = "";
    [ObservableProperty] private string _statTags = "";
    [ObservableProperty] private string _statLastActivity = "";

    // Research
    [ObservableProperty] private string _researchPrompt = "";
    [ObservableProperty] private int _targetSources = 8;
    [ObservableProperty] private bool _isResearchRunning;
    [ObservableProperty] private bool _isStreamlinedCodex;
    [ObservableProperty] private bool _showStreamlinedToggle;
    [ObservableProperty] private bool _showLiveProgress;
    [ObservableProperty] private bool _isResearchComplete;
    [ObservableProperty] private string _researchStatus = "";
    [ObservableProperty] private ObservableCollection<JobViewModel> _jobs = new();
    [ObservableProperty] private JobViewModel? _selectedJob;

    // Sources / Snapshots
    [ObservableProperty] private string _snapshotUrl = "";
    [ObservableProperty] private ObservableCollection<SnapshotViewModel> _snapshots = new();
    [ObservableProperty] private SnapshotViewModel? _selectedSnapshot;
    [ObservableProperty] private string _snapshotViewerContent = "";

    // Notebook
    [ObservableProperty] private ObservableCollection<NotebookEntryViewModel> _notebookEntries = new();
    [ObservableProperty] private string _newNoteTitle = "";
    [ObservableProperty] private string _newNoteContent = "";

    // Search / Evidence
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private ObservableCollection<EvidenceItemViewModel> _evidenceResults = new();
    [ObservableProperty] private ObservableCollection<EvidenceItemViewModel> _pinnedEvidence = new();

    // Reports
    [ObservableProperty] private ObservableCollection<ReportViewModel> _reports = new();
    [ObservableProperty] private ReportViewModel? _selectedReport;
    [ObservableProperty] private string _reportContent = "";

    // Replay
    [ObservableProperty] private ObservableCollection<ReplayEntryViewModel> _replayEntries = new();

    // Discovery Studio
    [ObservableProperty] private string _discoveryProblem = "";
    [ObservableProperty] private string _discoveryConstraints = "";
    [ObservableProperty] private ObservableCollection<IdeaCardViewModel> _ideaCards = new();
    [ObservableProperty] private bool _isDiscoveryRunning;

    // Materials Explorer
    [ObservableProperty] private string _materialProperties = "";
    [ObservableProperty] private string _materialFilters = "";
    [ObservableProperty] private string _materialInclude = "";
    [ObservableProperty] private string _materialAvoid = "";
    [ObservableProperty] private ObservableCollection<MaterialCandidateViewModel> _materialCandidates = new();
    [ObservableProperty] private bool _isMaterialsRunning;
    [ObservableProperty] private string _materialComparisonTable = "";

    // Programming IP
    [ObservableProperty] private string _programmingProblem = "";
    [ObservableProperty] private ObservableCollection<ApproachViewModel> _approaches = new();
    [ObservableProperty] private bool _isProgrammingRunning;
    [ObservableProperty] private string _programmingReport = "";

    // Fusion
    [ObservableProperty] private string _fusionPrompt = "";
    [ObservableProperty] private FusionMode _fusionMode = FusionMode.Blend;
    [ObservableProperty] private ObservableCollection<FusionResultViewModel> _fusionResults = new();
    [ObservableProperty] private bool _isFusionRunning;
    [ObservableProperty] private FusionPromptTemplate? _selectedFusionTemplate;
    public ObservableCollection<FusionPromptTemplate> FusionPromptTemplates { get; } = new(FusionPromptTemplate.BuiltIn);

    // Artifacts
    [ObservableProperty] private ObservableCollection<ArtifactViewModel> _artifacts = new();

    // Q&A
    [ObservableProperty] private string _qaQuestion = "";
    [ObservableProperty] private string _qaScope = "session";
    [ObservableProperty] private ObservableCollection<QaScopeOption> _qaScopeOptions = new();
    [ObservableProperty] private ObservableCollection<QaMessageViewModel> _qaMessages = new();
    [ObservableProperty] private bool _isQaRunning;

    // Source Quality & Time Range
    [ObservableProperty] private bool _isSourceQualityOn;
    [ObservableProperty] private string _selectedTimeRange = "Any time";
    public string[] TimeRangeOptions { get; } = new[] { "Any time", "Past year", "Past month", "Past week", "Past day" };

    // OCR
    [ObservableProperty] private string _captureImagePath = "";
    [ObservableProperty] private ObservableCollection<CaptureViewModel> _captures = new();

    // Logs
    [ObservableProperty] private string _logContent = "";

    // Tab Navigation — filtered by domain pack
    [ObservableProperty] private ObservableCollection<TabItemViewModel> _visibleTabs = new();

    // Research Progress (live tracking)
    [ObservableProperty] private string _progressStep = "";
    [ObservableProperty] private int _progressSourcesFound;
    [ObservableProperty] private int _progressSourcesFailed;
    [ObservableProperty] private int _progressTargetSources;
    [ObservableProperty] private double _progressCoverage;
    [ObservableProperty] private string _progressCoverageDisplay = "";
    [ObservableProperty] private int _progressIteration;
    [ObservableProperty] private int _progressMaxIterations;
    [ObservableProperty] private string _iterationDisplay = "";
    [ObservableProperty] private ObservableCollection<string> _liveLogLines = new();
    [ObservableProperty] private ObservableCollection<SourceHealthViewModel> _sourceHealthItems = new();

    // Search engine health (per-engine success/failure during research)
    [ObservableProperty] private ObservableCollection<SearchEngineHealthViewModel> _searchEngineHealthItems = new();

    // C1: Enhanced progress
    [ObservableProperty] private int _subQuestionsTotal;
    [ObservableProperty] private int _subQuestionsAnswered;
    [ObservableProperty] private double _groundingScore;
    [ObservableProperty] private string _groundingScoreDisplay = "";
    [ObservableProperty] private string _subQuestionStatus = "";
    [ObservableProperty] private int _browserPoolAvailable;
    [ObservableProperty] private int _browserPoolTotal;

    // Post-research discoverability hints
    [ObservableProperty] private string _postResearchTip = "";
    [ObservableProperty] private bool _hasPostResearchTip;

    // Sorting
    [ObservableProperty] private string _snapshotSortMode = "Newest";
    [ObservableProperty] private string _evidenceSortMode = "Score";

    // Cross-Session Search
    [ObservableProperty] private string _globalSearchQuery = "";
    [ObservableProperty] private ObservableCollection<CrossSessionResultViewModel> _globalSearchResults = new();
    [ObservableProperty] private bool _isGlobalSearchRunning;
    [ObservableProperty] private string _globalSearchStatus = "";
    [ObservableProperty] private bool _globalSearchReports;
    [ObservableProperty] private string _globalStatsText = "";
    [ObservableProperty] private ObservableCollection<CrossSessionReportResultViewModel> _globalReportResults = new();

    // Hive Mind
    [ObservableProperty] private string _hiveMindQuestion = "";
    [ObservableProperty] private string _hiveMindAnswer = "";
    [ObservableProperty] private bool _isHiveMindBusy;
    [ObservableProperty] private string _hiveMindStatus = "";
    [ObservableProperty] private string _hiveMindStatsText = "";

    // Hive Mind Curation
    [ObservableProperty] private ObservableCollection<GlobalChunkViewModel> _hiveMindChunks = new();
    [ObservableProperty] private GlobalChunkViewModel? _selectedHiveMindChunk;
    [ObservableProperty] private string _hiveMindSourceTypeFilter = "";
    [ObservableProperty] private int _hiveMindPageIndex;
    [ObservableProperty] private bool _hiveMindHasMorePages;

    // Citation Verification
    [ObservableProperty] private ObservableCollection<CitationVerificationViewModel> _citationVerifications = new();
    [ObservableProperty] private string _verificationSummaryText = "";
    [ObservableProperty] private bool _isCitationVerifying;
    [ObservableProperty] private bool _isDeepVerifying;

    // Contradiction Detection
    [ObservableProperty] private ObservableCollection<ContradictionViewModel> _contradictions = new();
    [ObservableProperty] private bool _isContradictionRunning;
    [ObservableProperty] private string _contradictionStatus = "";
    [ObservableProperty] private bool _isDeepContradictionRunning;

    // Incremental Research
    [ObservableProperty] private string _continuePrompt = "";
    [ObservableProperty] private int _additionalSources = 3;
    [ObservableProperty] private bool _isContinueRunning;

    // Research Comparison
    [ObservableProperty] private ObservableCollection<JobViewModel> _compareJobsA = new();
    [ObservableProperty] private ObservableCollection<JobViewModel> _compareJobsB = new();
    [ObservableProperty] private JobViewModel? _selectedCompareA;
    [ObservableProperty] private JobViewModel? _selectedCompareB;
    [ObservableProperty] private string _comparisonResultMarkdown = "";
    [ObservableProperty] private bool _isComparing;

    // Repo Intelligence
    [ObservableProperty] private string _repoUrl = "";
    [ObservableProperty] private string _repoUrlList = "";
    [ObservableProperty] private ObservableCollection<RepoProfileViewModel> _repoProfiles = new();
    [ObservableProperty] private bool _isRepoScanning;
    [ObservableProperty] private string _repoScanStatus = "";

    // Repo Q&A — ask questions about any repo
    [ObservableProperty] private string _repoAskUrl = "";
    [ObservableProperty] private string _repoAskQuestion = "";
    [ObservableProperty] private string _repoAskAnswer = "";
    [ObservableProperty] private bool _isRepoAsking;
    [ObservableProperty] private ObservableCollection<RepoQaMessageViewModel> _repoQaHistory = new();

    // Project Discovery — search GitHub, one-click scan
    [ObservableProperty] private string _discoveryQuery = "";
    [ObservableProperty] private string _discoveryLanguageFilter = "";
    [ObservableProperty] private int _discoveryMinStars;
    [ObservableProperty] private ObservableCollection<DiscoveryResultViewModel> _discoveryResults = new();
    [ObservableProperty] private bool _isDiscoverySearching;
    [ObservableProperty] private string _discoveryStatus = "";

    // Project Fusion
    [ObservableProperty] private ObservableCollection<ProjectFusionArtifactViewModel> _projectFusions = new();
    [ObservableProperty] private ObservableCollection<FusionInputOption> _fusionInputOptions = new();
    [ObservableProperty] private ProjectFusionGoal _projectFusionGoal = ProjectFusionGoal.Merge;
    [ObservableProperty] private string _projectFusionFocus = "";
    [ObservableProperty] private bool _isProjectFusing;
    [ObservableProperty] private ProjectFusionTemplate? _selectedProjectFusionTemplate;
    public ObservableCollection<ProjectFusionTemplate> ProjectFusionTemplates { get; } = new(ProjectFusionTemplate.BuiltIn);
    public Array ProjectFusionGoals => Enum.GetValues(typeof(ProjectFusionGoal));

    public Array FusionModes => Enum.GetValues(typeof(FusionMode));

    public SessionWorkspaceViewModel(
        string sessionId,
        SessionManager sessionManager,
        ArtifactStore artifactStore,
        SnapshotService snapshotService,
        OcrService ocrService,
        IndexService indexService,
        RetrievalService retrievalService,
        ResearchJobRunner researchRunner,
        DiscoveryJobRunner discoveryRunner,
        ProgrammingJobRunner programmingRunner,
        MaterialsJobRunner materialsRunner,
        FusionJobRunner fusionRunner,
        ExportService exportService,
        LlmService llmService,
        AppSettings appSettings,
        IDialogService dialogService,
        CrossSessionSearchService crossSearch,
        CitationVerificationService citationVerifier,
        ContradictionDetector contradictionDetector,
        ResearchComparisonService comparisonService,
        RepoIntelligenceJobRunner repoRunner,
        ProjectFusionEngine projectFusionEngine,
        GlobalMemoryService globalMemory,
        NotificationService notificationService,
        GitHubDiscoveryService discoveryService)
    {
        _sessionId = sessionId;
        _sessionManager = sessionManager;
        _artifactStore = artifactStore;
        _snapshotService = snapshotService;
        _ocrService = ocrService;
        _indexService = indexService;
        _retrievalService = retrievalService;
        _researchRunner = researchRunner;
        _discoveryRunner = discoveryRunner;
        _programmingRunner = programmingRunner;
        _materialsRunner = materialsRunner;
        _fusionRunner = fusionRunner;
        _exportService = exportService;
        _llmService = llmService;
        _appSettings = appSettings;
        _dialogService = dialogService;
        _crossSearch = crossSearch;
        _citationVerifier = citationVerifier;
        _contradictionDetector = contradictionDetector;
        _comparisonService = comparisonService;
        _repoRunner = repoRunner;
        _projectFusionEngine = projectFusionEngine;
        _globalMemory = globalMemory;
        _notificationService = notificationService;
        _discoveryService = discoveryService;

        // Initialize streamlined toggle from settings — only show when Codex OAuth is active
        IsStreamlinedCodex = appSettings.StreamlinedCodexMode;
        ShowStreamlinedToggle = llmService.IsCodexOAuthActive;

        // Initialize source quality & time range from settings
        IsSourceQualityOn = appSettings.SourceQualityRanking;
        SelectedTimeRange = appSettings.SearchTimeRange switch
        {
            "year" => "Past year",
            "month" => "Past month",
            "week" => "Past week",
            "day" => "Past day",
            _ => "Any time"
        };

        // Initialize Q&A scope options
        QaScopeOptions.Add(new QaScopeOption("session", "Entire Session"));

        _session = sessionManager.GetSession(sessionId) ?? throw new InvalidOperationException("Session not found");
        InitializeVisibleTabs(_session.Pack);
        LoadSessionData();

        // Auto-save timer: checks every 30 seconds for dirty notebook entries
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveNotebook();
        _autoSaveTimer.Start();
    }

    /// <summary>
    /// Populates VisibleTabs based on the session's domain pack.
    /// Core tabs (Overview, Research, Snapshots, Evidence, Notebook, Reports, Replay, Export, Logs)
    /// are always visible. Domain-specific tabs (Materials, Programming, OCR) are filtered.
    /// Analysis tabs (Discovery, Fusion, Artifacts) are always visible.
    /// </summary>
    private void InitializeVisibleTabs(DomainPack pack)
    {
        var allTabs = new TabItemViewModel[]
        {
            new("📋", "Overview",      "Overview",      "Core"),
            new("🔬", "Research",      "Research",      "Core"),
            new("🌐", "Snapshots",     "Snapshots",     "Core"),
            new("📷", "OCR",           "OCR",           "Tools"),
            new("🔍", "Evidence",      "Evidence",      "Core"),
            new("📓", "Notebook",      "Notebook",      "Core"),
            new("📊", "Reports",       "Reports",       "Core"),
            new("💬", "Q&A",           "QA",            "Core"),
            new("⏪", "Replay",        "Replay",        "Core"),
            new("💡", "Discovery",     "Discovery",     "Analysis"),
            new("🧪", "Materials",     "Materials",     "Tools"),
            new("💻", "Programming",   "Programming",   "Tools"),
            new("🔗", "Fusion",        "Fusion",        "Analysis"),
            new("📦", "Artifacts",     "Artifacts",     "Analysis"),
            new("📜", "Logs",          "Logs",          "Meta"),
            new("📤", "Export",        "Export",        "Meta"),
            new("✅", "Verify",        "Verify",        "Analysis"),
            new("⚡", "Contradictions","Contradictions", "Analysis"),
            new("📈", "Compare",       "Compare",       "Analysis"),
            new("🧠", "Hive Mind",     "GlobalSearch",  "Meta"),
            new("🔎", "Repo Scan",     "RepoScan",      "Tools"),
            new("🏗️", "Project Fusion","ProjectFusion",  "Analysis"),
        };

        // Tabs hidden per domain pack
        // Repo Scan + Project Fusion visible for ProgrammingResearchIP and RepoIntelligence packs
        var hiddenTabs = pack switch
        {
            DomainPack.GeneralResearch      => new HashSet<string> { "Materials", "Programming", "RepoScan", "ProjectFusion" },
            DomainPack.HistoryPhilosophy    => new HashSet<string> { "Materials", "Programming", "RepoScan", "ProjectFusion" },
            DomainPack.Math                 => new HashSet<string> { "Materials", "Programming", "OCR", "RepoScan", "ProjectFusion" },
            DomainPack.MakerMaterials       => new HashSet<string> { "Programming", "RepoScan", "ProjectFusion" },
            DomainPack.ChemistrySafe        => new HashSet<string> { "Programming", "RepoScan", "ProjectFusion" },
            DomainPack.ProgrammingResearchIP => new HashSet<string> { "Materials", "OCR" },
            DomainPack.RepoIntelligence     => new HashSet<string> { "Materials", "OCR" },
            _ => new HashSet<string>()
        };

        VisibleTabs.Clear();
        foreach (var tab in allTabs)
        {
            if (!hiddenTabs.Contains(tab.Tag))
                VisibleTabs.Add(tab);
        }
    }

    private void AutoSaveNotebook()
    {
        if (!_notebookDirty) return;
        _notebookDirty = false;

        try
        {
            var db = _sessionManager.GetSessionDb(_sessionId);
            foreach (var entry in NotebookEntries)
            {
                entry.Entry.UpdatedUtc = DateTime.UtcNow;
                db.SaveNotebookEntry(entry.Entry);
            }
        }
        catch { /* auto-save should not throw */ }
    }

    /// <summary>
    /// Mark notebook as dirty when content changes. Call from the note editing UI.
    /// </summary>
    [RelayCommand]
    private void MarkNotebookDirty()
    {
        _notebookDirty = true;
    }

    private void RefreshQaScopeOptions()
    {
        var current = QaScope;
        QaScopeOptions.Clear();
        QaScopeOptions.Add(new QaScopeOption("session", "Entire Session"));
        foreach (var r in Reports)
        {
            var label = r.Report.Content?.Length > 60
                ? r.Report.Content[..60].Replace('\n', ' ') + "…"
                : r.Report.Id;
            QaScopeOptions.Add(new QaScopeOption(r.Report.Id, $"Report: {label}"));
        }
        // Restore selection if it still exists
        if (QaScopeOptions.Any(o => o.Value == current))
            QaScope = current;
        else
            QaScope = "session";
    }

    private void LoadSessionData()
    {
        var db = _sessionManager.GetSessionDb(_sessionId);

        // Overview stats — updated after all collections are loaded
        StatDomainPack = Session.Pack.ToDisplayName();
        StatStatus = Session.Status.ToDisplayName();
        StatTags = Session.Tags.Count > 0 ? string.Join(", ", Session.Tags) : "(none)";
        StatLastActivity = Session.UpdatedUtc.ToLocalTime().ToString("g");

        // Load jobs
        Jobs.Clear();
        foreach (var j in db.GetJobs())
            Jobs.Add(new JobViewModel(j));

        // Sync comparison job lists
        CompareJobsA.Clear();
        CompareJobsB.Clear();
        foreach (var jvm in Jobs)
        {
            CompareJobsA.Add(jvm);
            CompareJobsB.Add(jvm);
        }

        // Load snapshots
        Snapshots.Clear();
        foreach (var s in db.GetSnapshots())
            Snapshots.Add(new SnapshotViewModel(s));

        // Load reports
        Reports.Clear();
        foreach (var r in db.GetReports())
            Reports.Add(new ReportViewModel(r));

        // Refresh Q&A scope options with loaded reports
        RefreshQaScopeOptions();

        // Load notebook
        NotebookEntries.Clear();
        foreach (var n in db.GetNotebookEntries())
            NotebookEntries.Add(new NotebookEntryViewModel(n));

        // Load artifacts
        Artifacts.Clear();
        foreach (var a in db.GetArtifacts())
            Artifacts.Add(new ArtifactViewModel(a));

        // Load captures
        Captures.Clear();
        foreach (var c in db.GetCaptures())
            Captures.Add(new CaptureViewModel(c));

        // Load idea cards
        IdeaCards.Clear();
        foreach (var ic in db.GetIdeaCards())
            IdeaCards.Add(new IdeaCardViewModel(ic));

        // Load material candidates
        MaterialCandidates.Clear();
        foreach (var mc in db.GetMaterialCandidates())
            MaterialCandidates.Add(new MaterialCandidateViewModel(mc));

        // Load fusion results
        FusionResults.Clear();
        foreach (var fr in db.GetFusionResults())
            FusionResults.Add(new FusionResultViewModel(fr));

        // Load repo profiles
        RepoProfiles.Clear();
        foreach (var rp in db.GetRepoProfiles())
            RepoProfiles.Add(new RepoProfileViewModel(rp));

        // Load project fusions
        ProjectFusions.Clear();
        foreach (var pf in db.GetProjectFusions())
            ProjectFusions.Add(new ProjectFusionArtifactViewModel(pf));

        // Refresh fusion input options (repo profiles + prior fusions)
        RefreshFusionInputOptions();

        // Load pinned evidence from DB
        PinnedEvidence.Clear();
        foreach (var pe in db.GetPinnedEvidence())
        {
            PinnedEvidence.Add(new EvidenceItemViewModel
            {
                ChunkId = pe.ChunkId, SourceId = pe.SourceId, SourceType = pe.SourceType,
                Text = pe.Text, Score = pe.Score, SourceUrl = pe.SourceUrl
            });
        }

        // Load Q&A messages from DB
        QaMessages.Clear();
        foreach (var qa in db.GetQaMessages())
            QaMessages.Add(new QaMessageViewModel { Question = qa.Question, Answer = qa.Answer, ModelUsed = qa.ModelUsed });

        // Update overview stats
        StatSources = Snapshots.Count;
        StatEvidence = db.GetChunkCount();
        StatReports = Reports.Count;
        StatNotes = NotebookEntries.Count;
        StatJobs = Jobs.Count;
        StatPins = PinnedEvidence.Count;
        OverviewText = Session.LastReportSummary ?? "Run a research job to get started.";
    }

    private void RefreshFusionInputOptions()
    {
        FusionInputOptions.Clear();
        foreach (var rp in RepoProfiles)
            FusionInputOptions.Add(new FusionInputOption(rp.Profile.Id, FusionInputType.RepoProfile, $"📦 {rp.Owner}/{rp.RepoName}"));
        foreach (var pf in ProjectFusions)
            FusionInputOptions.Add(new FusionInputOption(pf.Artifact.Id, FusionInputType.FusionArtifact, $"🏗️ {pf.Title}"));
    }
}