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
    [ObservableProperty] private int _targetSources = 5;
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
        ResearchComparisonService comparisonService)
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
            new("🌍", "Global Search", "GlobalSearch",  "Meta"),
        };

        // Tabs hidden per domain pack
        var hiddenTabs = pack switch
        {
            DomainPack.GeneralResearch      => new HashSet<string> { "Materials", "Programming" },
            DomainPack.HistoryPhilosophy    => new HashSet<string> { "Materials", "Programming" },
            DomainPack.Math                 => new HashSet<string> { "Materials", "Programming", "OCR" },
            DomainPack.MakerMaterials       => new HashSet<string> { "Programming" },
            DomainPack.ChemistrySafe        => new HashSet<string> { "Programming" },
            DomainPack.ProgrammingResearchIP => new HashSet<string> { "Materials", "OCR" },
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
            QaMessages.Add(new QaMessageViewModel { Question = qa.Question, Answer = qa.Answer });

        // Update overview stats
        StatSources = Snapshots.Count;
        StatEvidence = db.GetChunkCount();
        StatReports = Reports.Count;
        StatNotes = NotebookEntries.Count;
        StatJobs = Jobs.Count;
        StatPins = PinnedEvidence.Count;
        OverviewText = Session.LastReportSummary ?? "Run a research job to get started.";
    }

    // ---- Research Commands ----
    [RelayCommand]
    private async Task RunResearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ResearchPrompt)) return;
        IsResearchRunning = true;
        IsResearchComplete = false;
        ShowLiveProgress = true;
        ResearchStatus = "Starting research...";
        _jobCts = new CancellationTokenSource();

        // Clear live progress
        LiveLogLines.Clear();
        SourceHealthItems.Clear();
        ProgressStep = "Initializing…";
        ProgressSourcesFound = 0;
        ProgressSourcesFailed = 0;
        ProgressTargetSources = TargetSources;
        ProgressCoverage = 0;
        ProgressCoverageDisplay = "0%";
        ProgressIteration = 0;
        ProgressMaxIterations = 0;

        // Subscribe to progress
        _researchRunner.ProgressChanged += OnResearchProgress;

        try
        {
            var job = await _researchRunner.RunAsync(_sessionId, ResearchPrompt, JobType.Research, TargetSources, _jobCts.Token);
            ResearchStatus = $"Completed: {job.State}";
            LoadSessionData();

            // Auto-select the latest full report so the user sees the new results immediately
            var latestFullReport = Reports
                .Where(r => r.Report.ReportType == "full")
                .OrderByDescending(r => r.Report.CreatedUtc)
                .FirstOrDefault();
            if (latestFullReport != null)
            {
                SelectedReport = latestFullReport;
                ActiveTab = "Reports";
            }

            // Show discoverability hints after successful research
            if (job.State == JobState.Completed && Snapshots.Count > 0)
            {
                ShowPostResearchTips();
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _researchRunner.ProgressChanged -= OnResearchProgress;
            IsResearchRunning = false;
            IsResearchComplete = true;
            IterationDisplay = ProgressIteration > 0
                ? $"Done in {ProgressIteration} iteration{(ProgressIteration > 1 ? "s" : "")}"
                : "";
            // ShowLiveProgress stays true — user can still see final stats & log
        }
    }

    private void OnResearchProgress(object? sender, JobProgressEventArgs e)
    {
        // Must dispatch to UI thread
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            ProgressStep = e.StepDescription;
            ProgressSourcesFound = e.SourcesFound;
            ProgressSourcesFailed = e.SourcesFailed;
            ProgressTargetSources = e.TargetSources;
            ProgressCoverage = e.CoverageScore;
            ProgressCoverageDisplay = $"{e.CoverageScore:P0}";
            ProgressIteration = e.CurrentIteration;
            ProgressMaxIterations = e.MaxIterations;
            IterationDisplay = e.CurrentIteration > 0
                ? $"Iteration {e.CurrentIteration} / {e.MaxIterations}"
                : "";
            ResearchStatus = e.StepDescription;

            // C1: Enhanced progress fields
            SubQuestionsTotal = e.SubQuestionsTotal;
            SubQuestionsAnswered = e.SubQuestionsAnswered;
            GroundingScore = e.GroundingScore;
            GroundingScoreDisplay = e.SubQuestionsTotal > 0 || e.GroundingScore > 0
                ? $"{e.GroundingScore:P0}" : "—";
            SubQuestionStatus = e.SubQuestionsTotal > 0 ? $"{e.SubQuestionsAnswered}/{e.SubQuestionsTotal}" : "";
            BrowserPoolAvailable = e.BrowserPoolAvailable;
            BrowserPoolTotal = e.BrowserPoolTotal;

            if (!string.IsNullOrEmpty(e.LogMessage))
            {
                LiveLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {e.LogMessage}");
                // Keep log manageable
                while (LiveLogLines.Count > 200)
                    LiveLogLines.RemoveAt(0);
            }

            // Update source health
            if (e.SourceHealth?.Count > 0)
            {
                SourceHealthItems.Clear();
                foreach (var sh in e.SourceHealth)
                    SourceHealthItems.Add(new SourceHealthViewModel(sh));
            }
        });
    }

    [RelayCommand]
    private void CancelResearch()
    {
        if (!_dialogService.Confirm("Are you sure you want to cancel the running research?", "Cancel Research"))
            return;

        if (SelectedJob != null)
        {
            _researchRunner.CancelJob(_sessionId, SelectedJob.Job.Id);
        }
        _jobCts?.Cancel();
        ResearchStatus = "Cancelled";
    }

    [RelayCommand]
    private void PauseResearch()
    {
        if (SelectedJob != null)
        {
            _researchRunner.PauseJob(_sessionId, SelectedJob.Job.Id);
            ResearchStatus = "Paused";
            ProgressStep = "Paused";
            LoadSessionData();
        }
    }

    [RelayCommand]
    private async Task ResumeResearchAsync()
    {
        if (SelectedJob == null) return;
        IsResearchRunning = true;
        IsResearchComplete = false;
        ShowLiveProgress = true;
        _jobCts = new CancellationTokenSource();
        ProgressStep = "Resuming…";
        _researchRunner.ProgressChanged += OnResearchProgress;

        try
        {
            var job = await _researchRunner.ResumeAsync(_sessionId, SelectedJob.Job.Id, _jobCts.Token);
            ResearchStatus = $"Completed: {job?.State}";
            LoadSessionData();

            // Auto-select the latest full report
            var latestResume = Reports
                .Where(r => r.Report.ReportType == "full")
                .OrderByDescending(r => r.Report.CreatedUtc)
                .FirstOrDefault();
            if (latestResume != null)
            {
                SelectedReport = latestResume;
                ActiveTab = "Reports";
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _researchRunner.ProgressChanged -= OnResearchProgress;
            IsResearchRunning = false;
            IsResearchComplete = true;
        }
    }

    // ---- Snapshot Commands ----

    private void ShowPostResearchTips()
    {
        var tips = new List<string>();
        tips.Add("Research complete! Here's what you can do next:");
        tips.Add("  • Evidence tab — Search across all indexed content with hybrid BM25 + semantic search");
        tips.Add("  • Reports tab — View synthesized final and interim reports with citations");

        // Check which tabs are visible to suggest appropriate features
        var tabTags = VisibleTabs.Select(t => t.Tag).ToHashSet();
        if (tabTags.Contains("Discovery"))
            tips.Add("  • Discovery Studio — Generate novel hypotheses and cross-domain ideas");
        if (tabTags.Contains("Fusion"))
            tips.Add("  • Idea Fusion — Blend or contrast findings across different angles");
        if (tabTags.Contains("Artifacts"))
            tips.Add("  • Artifacts — Review auto-generated tables, formulas, and code snippets");
        if (tabTags.Contains("Programming"))
            tips.Add("  • Programming IP — Analyze patents and prior art for technical approaches");
        if (tabTags.Contains("Materials"))
            tips.Add("  • Materials Explorer — Search for candidate materials matching your criteria");

        PostResearchTip = string.Join("\n", tips);
        HasPostResearchTip = true;
    }

    [RelayCommand]
    private void DismissTip()
    {
        HasPostResearchTip = false;
        PostResearchTip = "";
    }

    [RelayCommand]
    private void SortSnapshots(string mode)
    {
        SnapshotSortMode = mode;
        var sorted = mode switch
        {
            "Oldest" => Snapshots.OrderBy(s => s.Snapshot.CapturedUtc).ToList(),
            "A-Z" => Snapshots.OrderBy(s => s.Snapshot.Title ?? s.Snapshot.Url).ToList(),
            _ => Snapshots.OrderByDescending(s => s.Snapshot.CapturedUtc).ToList() // Newest
        };
        Snapshots.Clear();
        foreach (var s in sorted) Snapshots.Add(s);
    }

    [RelayCommand]
    private void SortEvidence(string mode)
    {
        EvidenceSortMode = mode;
        var sorted = mode switch
        {
            "A-Z" => EvidenceResults.OrderBy(e => e.Text).ToList(),
            "Source" => EvidenceResults.OrderBy(e => e.SourceId).ToList(),
            _ => EvidenceResults.OrderByDescending(e => e.Score).ToList() // Score
        };
        EvidenceResults.Clear();
        foreach (var e in sorted) EvidenceResults.Add(e);
    }

    [RelayCommand]
    private async Task CaptureSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(SnapshotUrl)) return;
        try
        {
            var snapshot = await _snapshotService.CaptureUrlAsync(_sessionId, SnapshotUrl);
            if (!snapshot.IsBlocked)
            {
                await _indexService.IndexSnapshotAsync(_sessionId, snapshot);
            }
            Snapshots.Insert(0, new SnapshotViewModel(snapshot));
            SnapshotUrl = "";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Snapshot error: {ex.Message}";
        }
    }

    partial void OnIsStreamlinedCodexChanged(bool value)
    {
        _appSettings.StreamlinedCodexMode = value;
    }

    partial void OnIsSourceQualityOnChanged(bool value)
    {
        _appSettings.SourceQualityRanking = value;
    }

    partial void OnSelectedTimeRangeChanged(string value)
    {
        _appSettings.SearchTimeRange = value switch
        {
            "Past year" => "year",
            "Past month" => "month",
            "Past week" => "week",
            "Past day" => "day",
            _ => "any"
        };
    }

    partial void OnSelectedSnapshotChanged(SnapshotViewModel? value)
    {
        // Fire-and-forget async load — never block the UI thread with file I/O
        _ = LoadSnapshotContentAsync(value);
    }

    private async Task LoadSnapshotContentAsync(SnapshotViewModel? value)
    {
        try
        {
            if (value != null && File.Exists(value.Snapshot.TextPath))
            {
                SnapshotViewerContent = await File.ReadAllTextAsync(value.Snapshot.TextPath);
            }
            else if (value != null && File.Exists(value.Snapshot.HtmlPath))
            {
                var html = await File.ReadAllTextAsync(value.Snapshot.HtmlPath);
                SnapshotViewerContent = await Task.Run(() => SnapshotService.ExtractReadableText(html));
            }
            else
            {
                SnapshotViewerContent = value?.Snapshot.IsBlocked == true
                    ? $"Blocked: {value.Snapshot.BlockReason}"
                    : "No content available";
            }
        }
        catch (Exception ex)
        {
            SnapshotViewerContent = $"Error loading snapshot: {ex.Message}";
        }
    }

    // ---- OCR / Capture Commands ----
    [RelayCommand]
    private async Task CaptureScreenshotAsync()
    {
        if (string.IsNullOrWhiteSpace(CaptureImagePath) || !File.Exists(CaptureImagePath)) return;
        try
        {
            var capture = await _ocrService.CaptureScreenshotAsync(_sessionId, CaptureImagePath, "Manual capture");
            await _indexService.IndexCaptureAsync(_sessionId, capture);
            Captures.Insert(0, new CaptureViewModel(capture));
            CaptureImagePath = "";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"OCR error: {ex.Message}";
        }
    }

    // ---- Search/Evidence Commands ----
    [RelayCommand]
    private async Task SearchEvidenceAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        try
        {
            var results = await _retrievalService.HybridSearchAsync(_sessionId, SearchQuery);
            var db = _sessionManager.GetSessionDb(_sessionId);

            // Build a cache of SourceId → URL to avoid repeated lookups
            var urlCache = new Dictionary<string, string>();

            EvidenceResults.Clear();
            foreach (var r in results)
            {
                string sourceUrl = "";
                if (!urlCache.TryGetValue(r.SourceId, out sourceUrl!))
                {
                    var snapshot = db.GetSnapshot(r.SourceId);
                    sourceUrl = snapshot?.Url ?? "";
                    urlCache[r.SourceId] = sourceUrl;
                }

                EvidenceResults.Add(new EvidenceItemViewModel
                {
                    SourceId = r.SourceId,
                    SourceType = r.SourceType,
                    Score = r.Score,
                    Text = r.Chunk.Text,
                    ChunkId = r.Chunk.Id,
                    SourceUrl = sourceUrl
                });
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Search error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PinEvidence(EvidenceItemViewModel? item)
    {
        if (item != null && !PinnedEvidence.Contains(item))
        {
            PinnedEvidence.Add(item);
            var db = _sessionManager.GetSessionDb(_sessionId);
            db.SavePinnedEvidence(new PinnedEvidence
            {
                SessionId = _sessionId, ChunkId = item.ChunkId, SourceId = item.SourceId,
                SourceType = item.SourceType, Text = item.Text, Score = item.Score,
                SourceUrl = item.SourceUrl
            });
            StatPins = PinnedEvidence.Count;
        }
    }

    [RelayCommand]
    private void UnpinEvidence(EvidenceItemViewModel? item)
    {
        if (item != null)
        {
            PinnedEvidence.Remove(item);
            // Remove from DB by matching chunk ID
            var db = _sessionManager.GetSessionDb(_sessionId);
            var persisted = db.GetPinnedEvidence().FirstOrDefault(p => p.ChunkId == item.ChunkId);
            if (persisted != null)
                db.DeletePinnedEvidence(persisted.Id);
            StatPins = PinnedEvidence.Count;
        }
    }

    // ---- Notebook Commands ----
    [RelayCommand]
    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(NewNoteTitle)) return;
        var note = new NotebookEntry
        {
            SessionId = _sessionId,
            Title = NewNoteTitle,
            Content = NewNoteContent
        };
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.SaveNotebookEntry(note);
        NotebookEntries.Insert(0, new NotebookEntryViewModel(note));
        NewNoteTitle = "";
        NewNoteContent = "";
    }

    // ---- Delete Commands (remove clutter) ----

    [RelayCommand]
    private void DeleteJob(JobViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete job \"{item.Title}\" and all its reports, citations, and data?", "Delete Job"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteJob(item.Job.Id);
        LoadSessionData();
        ResearchStatus = "Job deleted.";
    }

    [RelayCommand]
    private void DeleteReport(ReportViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete report \"{item.Title}\"?", "Delete Report"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteReport(item.Report.Id);
        // Also remove the file on disk
        if (File.Exists(item.Report.FilePath))
            try { File.Delete(item.Report.FilePath); } catch { }
        Reports.Remove(item);
        if (SelectedReport == item) { SelectedReport = null; ReportContent = ""; }
        ResearchStatus = "Report deleted.";
    }

    [RelayCommand]
    private void DeleteSnapshot(SnapshotViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete snapshot \"{item.Title}\"?", "Delete Snapshot"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteSnapshot(item.Snapshot.Id);
        // Remove files on disk
        foreach (var path in new[] { item.Snapshot.HtmlPath, item.Snapshot.TextPath, item.Snapshot.BundlePath })
        {
            if (File.Exists(path)) try { File.Delete(path); } catch { }
        }
        Snapshots.Remove(item);
        ResearchStatus = "Snapshot deleted.";
    }

    [RelayCommand]
    private void DeleteNotebookEntry(NotebookEntryViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete note \"{item.Title}\"?", "Delete Note"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteNotebookEntry(item.Entry.Id);
        NotebookEntries.Remove(item);
        ResearchStatus = "Note deleted.";
    }

    [RelayCommand]
    private void DeleteIdeaCard(IdeaCardViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteIdeaCard(item.Card.Id);
        IdeaCards.Remove(item);
        ResearchStatus = "Idea card removed.";
    }

    [RelayCommand]
    private void DeleteMaterialCandidate(MaterialCandidateViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteMaterialCandidate(item.Candidate.Id);
        MaterialCandidates.Remove(item);
        ResearchStatus = "Material removed.";
    }

    [RelayCommand]
    private void DeleteFusionResult(FusionResultViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteFusionResult(item.Result.Id);
        FusionResults.Remove(item);
        ResearchStatus = "Fusion result removed.";
    }

    [RelayCommand]
    private void DeleteArtifact(ArtifactViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm($"Delete artifact \"{item.Name}\"?", "Delete Artifact"))
            return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteArtifact(item.Artifact.Id);
        // Remove stored file
        if (File.Exists(item.Artifact.StorePath))
            try { File.Delete(item.Artifact.StorePath); } catch { }
        Artifacts.Remove(item);
        ResearchStatus = "Artifact deleted.";
    }

    [RelayCommand]
    private void DeleteCapture(CaptureViewModel? item)
    {
        if (item == null) return;
        var db = _sessionManager.GetSessionDb(_sessionId);
        db.DeleteCapture(item.Capture.Id);
        Captures.Remove(item);
        ResearchStatus = "Capture deleted.";
    }

    // ---- Q&A Commands ----
    [RelayCommand]
    private async Task AskFollowUpAsync()
    {
        var question = QaQuestion?.Trim();
        if (string.IsNullOrEmpty(question) || IsQaRunning) return;

        IsQaRunning = true;
        QaQuestion = "";

        var msg = new QaMessageViewModel { Question = question, Answer = "Thinking…" };
        QaMessages.Add(msg);

        try
        {
            // Build context based on scope
            string context;
            if (QaScope == "session")
            {
                var results = await _retrievalService.HybridSearchAsync(_sessionId, question, 10, CancellationToken.None);
                context = string.Join("\n\n", results.Select(r => r.Chunk.Text));
            }
            else
            {
                // Scope is a report ID — search that report's content
                var report = Reports.FirstOrDefault(r => r.Report.Id == QaScope);
                if (report != null)
                {
                    var results = await _retrievalService.SearchReportContentAsync(
                        report.Report.Content, question, 5, CancellationToken.None);
                    context = string.Join("\n\n", results.Select(r => r.Chunk.Text));
                }
                else
                {
                    context = "(No report content found for this scope.)";
                }
            }

            if (string.IsNullOrWhiteSpace(context))
                context = "(No relevant context found.)";

            var prompt = $"Answer the following question using ONLY the provided context.\n\n" +
                         $"Context:\n{context}\n\nQuestion: {question}\n\nAnswer:";

            var answer = await _llmService.GenerateAsync(prompt, ct: CancellationToken.None);
            msg.Answer = string.IsNullOrWhiteSpace(answer) ? "No answer could be generated." : answer.Trim();

            // Persist to DB
            var db = _sessionManager.GetSessionDb(_sessionId);
            db.SaveQaMessage(new QaMessage
            {
                SessionId = _sessionId, Question = question,
                Answer = msg.Answer, Scope = QaScope
            });
        }
        catch (Exception ex)
        {
            msg.Answer = $"Error: {ex.Message}";
        }
        finally
        {
            IsQaRunning = false;
        }
    }

    // ---- Report Commands ----
    partial void OnSelectedReportChanged(ReportViewModel? value)
    {
        ReportContent = value?.Report.Content ?? "";

        // Load replay entries for the report's job
        if (value != null)
        {
            ReplayEntries.Clear();
            var db = _sessionManager.GetSessionDb(_sessionId);
            var job = db.GetJob(value.Report.JobId);
            if (job != null)
            {
                foreach (var re in job.ReplayEntries)
                    ReplayEntries.Add(new ReplayEntryViewModel(re));
            }
        }
    }

    // ---- Artifact Ingestion ----
    [RelayCommand]
    private async Task IngestFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            var artifact = _artifactStore.IngestFile(_sessionId, filePath);
            await _indexService.IndexArtifactAsync(_sessionId, artifact);
            Artifacts.Insert(0, new ArtifactViewModel(artifact));
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Ingest error: {ex.Message}";
        }
    }

    /// <summary>
    /// Handle files dropped via drag-and-drop.
    /// </summary>
    public async Task HandleDroppedFilesAsync(string[] filePaths)
    {
        int ingested = 0;
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                await IngestFileAsync(path);
                ingested++;
            }
        }
        if (ingested > 0)
        {
            ResearchStatus = $"Ingested {ingested} file(s) via drag-and-drop";
            ActiveTab = "Artifacts";
        }
    }

    // ---- Discovery Studio Commands ----
    [RelayCommand]
    private async Task RunDiscoveryAsync()
    {
        if (string.IsNullOrWhiteSpace(DiscoveryProblem)) return;
        IsDiscoveryRunning = true;
        try
        {
            var job = await _discoveryRunner.RunAsync(_sessionId, DiscoveryProblem, DiscoveryConstraints);
            LoadSessionData();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Discovery error: {ex.Message}";
        }
        finally
        {
            IsDiscoveryRunning = false;
        }
    }

    // ---- Materials Explorer Commands ----
    [RelayCommand]
    private async Task RunMaterialsSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(MaterialProperties)) return;
        IsMaterialsRunning = true;
        try
        {
            var query = new MaterialsQuery();
            foreach (var prop in MaterialProperties.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = prop.Split(':', '=');
                if (parts.Length >= 2)
                    query.PropertyTargets[parts[0].Trim()] = parts[1].Trim();
            }
            if (!string.IsNullOrWhiteSpace(MaterialFilters))
            {
                foreach (var f in MaterialFilters.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = f.Split(':', '=');
                    if (parts.Length >= 2)
                        query.Filters[parts[0].Trim()] = parts[1].Trim();
                }
            }
            if (!string.IsNullOrWhiteSpace(MaterialAvoid))
                query.AvoidMaterials = MaterialAvoid.Split(',').Select(s => s.Trim()).ToList();
            if (!string.IsNullOrWhiteSpace(MaterialInclude))
                query.IncludeMaterials = MaterialInclude.Split(',').Select(s => s.Trim()).ToList();

            var job = await _materialsRunner.RunAsync(_sessionId, query);
            LoadSessionData();
            BuildMaterialComparisonTable();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Materials error: {ex.Message}";
        }
        finally
        {
            IsMaterialsRunning = false;
        }
    }

    // ---- Programming Research Commands ----
    [RelayCommand]
    private async Task RunProgrammingResearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ProgrammingProblem)) return;
        IsProgrammingRunning = true;
        try
        {
            var job = await _programmingRunner.RunAsync(_sessionId, ProgrammingProblem);
            ProgrammingReport = job.FullReport ?? "";
            LoadSessionData();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Programming error: {ex.Message}";
        }
        finally
        {
            IsProgrammingRunning = false;
        }
    }

    // ---- Fusion Commands ----

    partial void OnSelectedFusionTemplateChanged(FusionPromptTemplate? value)
    {
        if (value == null) return;
        FusionPrompt = value.Prompt;
        FusionMode = value.Mode;
    }

    [RelayCommand]
    private async Task RunFusionAsync()
    {
        if (string.IsNullOrWhiteSpace(FusionPrompt)) return;
        IsFusionRunning = true;
        try
        {
            var request = new FusionRequest
            {
                SessionId = _sessionId,
                Prompt = FusionPrompt,
                Mode = FusionMode
            };
            var job = await _fusionRunner.RunAsync(_sessionId, request);
            LoadSessionData();
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Fusion error: {ex.Message}";
        }
        finally
        {
            IsFusionRunning = false;
        }
    }

    // ---- Export Commands ----
    [RelayCommand]
    private async Task ExportSessionAsync()
    {
        try
        {
            ResearchStatus = "Exporting session archive…";
            var outputPath = Path.Combine(Session.WorkspacePath, "Exports");
            Directory.CreateDirectory(outputPath);
            var zipPath = await Task.Run(() => _exportService.ExportSessionToZip(_sessionId, outputPath));
            ResearchStatus = $"Exported to: {zipPath}";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportReportAsHtmlAsync()
    {
        if (SelectedReport == null) { ResearchStatus = "Select a report first"; return; }
        try
        {
            var outputPath = Path.Combine(Session.WorkspacePath, "Exports");
            Directory.CreateDirectory(outputPath);
            var filePath = await _exportService.ExportReportAsHtmlAsync(_sessionId, SelectedReport.Report.Id, outputPath);
            ResearchStatus = $"HTML exported: {filePath}";
        }
        catch (Exception ex)
        {
            ResearchStatus = $"HTML export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportResearchPacketAsync()
    {
        try
        {
            var outputPath = Path.Combine(Session.WorkspacePath, "Exports");
            Directory.CreateDirectory(outputPath);
            var packetFolder = await _exportService.ExportResearchPacketAsync(_sessionId, outputPath);
            ResearchStatus = $"Research Packet exported: {packetFolder}";

            // Open the index.html directly in the default browser
            var indexPath = Path.Combine(packetFolder, "index.html");
            if (File.Exists(indexPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = indexPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Packet export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        if (Directory.Exists(Session.WorkspacePath))
        {
            System.Diagnostics.Process.Start("explorer.exe", Session.WorkspacePath);
        }
    }

    // ===== Cross-Session Search =====
    [RelayCommand]
    private async Task SearchGlobalAsync()
    {
        if (string.IsNullOrWhiteSpace(GlobalSearchQuery)) return;
        IsGlobalSearchRunning = true;
        GlobalSearchStatus = "Searching across all sessions...";
        try
        {
            if (GlobalSearchReports)
            {
                var reportResults = await Task.Run(() => _crossSearch.SearchReports(GlobalSearchQuery));
                Application.Current.Dispatcher.Invoke(() =>
                {
                    GlobalSearchResults.Clear();
                    GlobalReportResults.Clear();
                    foreach (var r in reportResults)
                        GlobalReportResults.Add(new CrossSessionReportResultViewModel(r));
                    GlobalSearchStatus = $"Found {reportResults.Count} report match(es) across sessions";
                });
            }
            else
            {
                var results = await Task.Run(() => _crossSearch.SearchAll(GlobalSearchQuery));
                Application.Current.Dispatcher.Invoke(() =>
                {
                    GlobalSearchResults.Clear();
                    GlobalReportResults.Clear();
                    foreach (var r in results)
                        GlobalSearchResults.Add(new CrossSessionResultViewModel(r));
                    GlobalSearchStatus = $"Found {results.Count} evidence result(s) across sessions";
                });
            }
        }
        catch (Exception ex)
        {
            GlobalSearchStatus = $"Search failed: {ex.Message}";
        }
        finally { IsGlobalSearchRunning = false; }
    }

    [RelayCommand]
    private async Task LoadGlobalStatsAsync()
    {
        try
        {
            var stats = await Task.Run(() => _crossSearch.GetGlobalStats());
            var domainSummary = string.Join(", ", stats.SessionsByDomain.Select(kv => $"{kv.Key}: {kv.Value}"));
            GlobalStatsText = $"📊 {stats.TotalSessions} sessions • {stats.TotalEvidence:N0} evidence chunks • " +
                              $"{stats.TotalReports} reports • {stats.TotalSnapshots} snapshots\n" +
                              $"Domains: {domainSummary}";
        }
        catch (Exception ex)
        {
            GlobalStatsText = $"Stats unavailable: {ex.Message}";
        }
    }

    // ===== Citation Verification =====
    [RelayCommand]
    private async Task VerifyCitationsAsync()
    {
        if (SelectedReport == null) return;
        IsCitationVerifying = true;
        VerificationSummaryText = "Verifying citations (quick mode)...";
        try
        {
            var jobId = SelectedJob?.Job.Id ?? "";
            var verifications = await Task.Run(() =>
                _citationVerifier.VerifyReportQuick(_sessionId, SelectedReport.Report.Content ?? "", jobId));
            var summary = CitationVerificationService.Summarize(verifications);
            Application.Current.Dispatcher.Invoke(() =>
            {
                CitationVerifications.Clear();
                foreach (var v in verifications)
                    CitationVerifications.Add(new CitationVerificationViewModel(v));
                VerificationSummaryText = summary.StatusLabel;
            });
        }
        catch (Exception ex)
        {
            VerificationSummaryText = $"Verification failed: {ex.Message}";
        }
        finally { IsCitationVerifying = false; }
    }

    [RelayCommand]
    private async Task DeepVerifyCitationsAsync()
    {
        if (SelectedReport == null) return;
        IsDeepVerifying = true;
        VerificationSummaryText = "Deep-verifying citations with LLM...";
        try
        {
            var jobId = SelectedJob?.Job.Id ?? "";
            var verifications = await _citationVerifier.VerifyReportAsync(
                _sessionId, SelectedReport.Report.Content ?? "", jobId);
            var summary = CitationVerificationService.Summarize(verifications);
            Application.Current.Dispatcher.Invoke(() =>
            {
                CitationVerifications.Clear();
                foreach (var v in verifications)
                    CitationVerifications.Add(new CitationVerificationViewModel(v));
                VerificationSummaryText = $"[Deep] {summary.StatusLabel}";
            });
        }
        catch (Exception ex)
        {
            VerificationSummaryText = $"Deep verification failed: {ex.Message}";
        }
        finally { IsDeepVerifying = false; }
    }

    // ===== Contradiction Detection =====
    [RelayCommand]
    private async Task DetectContradictionsAsync()
    {
        IsContradictionRunning = true;
        ContradictionStatus = "Scanning evidence for contradictions (fast mode)...";
        try
        {
            var results = await Task.Run(() => _contradictionDetector.DetectQuick(_sessionId));
            Application.Current.Dispatcher.Invoke(() =>
            {
                Contradictions.Clear();
                foreach (var c in results)
                    Contradictions.Add(new ContradictionViewModel(c));
                ContradictionStatus = results.Count > 0
                    ? $"Found {results.Count} potential contradiction(s)"
                    : "No contradictions detected — evidence is consistent";
            });
        }
        catch (Exception ex)
        {
            ContradictionStatus = $"Detection failed: {ex.Message}";
        }
        finally { IsContradictionRunning = false; }
    }

    [RelayCommand]
    private async Task DeepDetectContradictionsAsync()
    {
        IsDeepContradictionRunning = true;
        ContradictionStatus = "Deep-scanning with embedding similarity + LLM verification...";
        try
        {
            _jobCts = new CancellationTokenSource();
            var results = await _contradictionDetector.DetectAsync(_sessionId, ct: _jobCts.Token);
            if (results.Count > 0)
            {
                var verified = await _contradictionDetector.VerifyWithLlmAsync(_sessionId, results, _jobCts.Token);
                results = verified;
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Contradictions.Clear();
                foreach (var c in results)
                    Contradictions.Add(new ContradictionViewModel(c));
                var llmCount = results.Count(c => c.LlmVerified);
                ContradictionStatus = results.Count > 0
                    ? $"[Deep] Found {results.Count} contradiction(s), {llmCount} LLM-confirmed"
                    : "No contradictions detected — evidence is consistent";
            });
        }
        catch (Exception ex)
        {
            ContradictionStatus = $"Deep detection failed: {ex.Message}";
        }
        finally { IsDeepContradictionRunning = false; }
    }

    // ===== Materials Property Comparison Table =====
    private void BuildMaterialComparisonTable()
    {
        if (MaterialCandidates.Count < 2)
        {
            MaterialComparisonTable = "";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Property Comparison\n");

        // Collect all property keys across all candidates
        var allKeys = MaterialCandidates
            .SelectMany(c => c.Candidate.Properties.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Header row
        sb.Append("| Property |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.Name} |");
        sb.AppendLine();

        // Separator
        sb.Append("|----------|");
        foreach (var _ in MaterialCandidates)
            sb.Append("----------|");
        sb.AppendLine();

        // Property rows
        foreach (var key in allKeys)
        {
            sb.Append($"| **{key}** |");
            foreach (var c in MaterialCandidates)
            {
                var val = c.Candidate.Properties.TryGetValue(key, out var v) ? v : "—";
                sb.Append($" {val} |");
            }
            sb.AppendLine();
        }

        // Summary rows
        sb.Append("| **Fit Score** |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.FitScore} |");
        sb.AppendLine();

        sb.Append("| **Safety** |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.Safety} |");
        sb.AppendLine();

        sb.Append("| **DIY** |");
        foreach (var c in MaterialCandidates)
            sb.Append($" {c.Candidate.DiyFeasibility} |");
        sb.AppendLine();

        MaterialComparisonTable = sb.ToString();
    }

    // ===== Incremental Research (Continue) =====
    [RelayCommand]
    private async Task ContinueResearchAsync()
    {
        if (SelectedJob == null) return;
        IsContinueRunning = true;
        ResearchStatus = "Continuing research with additional sources...";
        try
        {
            _jobCts = new CancellationTokenSource();
            await _researchRunner.ContinueResearchAsync(
                _sessionId,
                SelectedJob.Job.Id,
                string.IsNullOrWhiteSpace(ContinuePrompt) ? null : ContinuePrompt,
                AdditionalSources);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResearchStatus = "Research continued — new report generated";
                LoadSessionData();
            });
        }
        catch (Exception ex)
        {
            ResearchStatus = $"Continue failed: {ex.Message}";
        }
        finally { IsContinueRunning = false; }
    }

    // ===== Research Comparison =====
    [RelayCommand]
    private async Task CompareResearchAsync()
    {
        if (SelectedCompareA?.Job == null || SelectedCompareB?.Job == null) return;
        IsComparing = true;
        ComparisonResultMarkdown = "Comparing research runs...";
        try
        {
            var comparison = await Task.Run(() =>
                _comparisonService.CompareInSession(_sessionId, SelectedCompareA.Job.Id, SelectedCompareB.Job.Id));
            Application.Current.Dispatcher.Invoke(() =>
            {
                ComparisonResultMarkdown = comparison.SummaryMarkdown;
            });
        }
        catch (Exception ex)
        {
            ComparisonResultMarkdown = $"Comparison failed: {ex.Message}";
        }
        finally { IsComparing = false; }
    }

    [RelayCommand]
    private void ViewLogs()
    {
        var logPath = Path.Combine(Session.WorkspacePath, "Logs");
        if (Directory.Exists(logPath))
        {
            var logFiles = Directory.GetFiles(logPath, "*.jsonl");
            if (logFiles.Any())
                LogContent = File.ReadAllText(logFiles.First());
            else
                LogContent = "No log files yet.";
        }
        else
        {
            LogContent = "Log directory not found.";
        }
    }
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
