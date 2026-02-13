using ResearchHive.Core.Configuration;
using ResearchHive.Core.Data;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using System;

namespace ResearchHive.ViewModels;

public class ViewModelFactory
{
    private readonly SessionManager _sessionManager;
    private readonly ArtifactStore _artifactStore;
    private readonly SnapshotService _snapshotService;
    private readonly OcrService _ocrService;
    private readonly IndexService _indexService;
    private readonly RetrievalService _retrievalService;
    private readonly LlmService _llmService;
    private readonly ResearchJobRunner _researchJobRunner;
    private readonly DiscoveryJobRunner _discoveryJobRunner;
    private readonly ProgrammingJobRunner _programmingJobRunner;
    private readonly MaterialsJobRunner _materialsJobRunner;
    private readonly FusionJobRunner _fusionJobRunner;
    private readonly RepoIntelligenceJobRunner _repoRunner;
    private readonly ProjectFusionEngine _projectFusionEngine;
    private readonly ExportService _exportService;
    private readonly InboxWatcher _inboxWatcher;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogService;
    private readonly SecureKeyStore _keyStore;
    private readonly CodexCliService _codexCli;
    private readonly CrossSessionSearchService _crossSearch;
    private readonly CitationVerificationService _citationVerifier;
    private readonly ContradictionDetector _contradictionDetector;
    private readonly ResearchComparisonService _comparisonService;
    private readonly GlobalMemoryService _globalMemory;
    private readonly NotificationService _notificationService;
    private readonly GitHubDiscoveryService _discoveryService;

    public ViewModelFactory(
        SessionManager sessionManager,
        ArtifactStore artifactStore,
        SnapshotService snapshotService,
        OcrService ocrService,
        IndexService indexService,
        RetrievalService retrievalService,
        LlmService llmService,
        ResearchJobRunner researchJobRunner,
        DiscoveryJobRunner discoveryJobRunner,
        ProgrammingJobRunner programmingJobRunner,
        MaterialsJobRunner materialsJobRunner,
        FusionJobRunner fusionJobRunner,
        RepoIntelligenceJobRunner repoRunner,
        ProjectFusionEngine projectFusionEngine,
        ExportService exportService,
        InboxWatcher inboxWatcher,
        AppSettings settings,
        IDialogService dialogService,
        SecureKeyStore keyStore,
        CodexCliService codexCli,
        CrossSessionSearchService crossSearch,
        CitationVerificationService citationVerifier,
        ContradictionDetector contradictionDetector,
        ResearchComparisonService comparisonService,
        GlobalMemoryService globalMemory,
        NotificationService notificationService,
        GitHubDiscoveryService discoveryService)
    {
        _sessionManager = sessionManager;
        _artifactStore = artifactStore;
        _snapshotService = snapshotService;
        _ocrService = ocrService;
        _indexService = indexService;
        _retrievalService = retrievalService;
        _llmService = llmService;
        _researchJobRunner = researchJobRunner;
        _discoveryJobRunner = discoveryJobRunner;
        _programmingJobRunner = programmingJobRunner;
        _materialsJobRunner = materialsJobRunner;
        _fusionJobRunner = fusionJobRunner;
        _repoRunner = repoRunner;
        _projectFusionEngine = projectFusionEngine;
        _exportService = exportService;
        _inboxWatcher = inboxWatcher;
        _settings = settings;
        _dialogService = dialogService;
        _keyStore = keyStore;
        _codexCli = codexCli;
        _crossSearch = crossSearch;
        _citationVerifier = citationVerifier;
        _contradictionDetector = contradictionDetector;
        _comparisonService = comparisonService;
        _globalMemory = globalMemory;
        _notificationService = notificationService;
        _discoveryService = discoveryService;
    }

    public MainViewModel CreateMainViewModel()
    {
        return new MainViewModel(_sessionManager, this, _llmService, _settings);
    }

    /// <summary>Expose GlobalMemoryService for startup orphan purge.</summary>
    public GlobalMemoryService? GetGlobalMemory() => _globalMemory;

    public SessionsSidebarViewModel CreateSessionsSidebar(Action<string> onSessionSelected, Action<string>? onSessionDeleted = null)
    {
        return new SessionsSidebarViewModel(_sessionManager, this, onSessionSelected, _dialogService, onSessionDeleted, _globalMemory);
    }

    public SessionWorkspaceViewModel CreateSessionWorkspace(string sessionId)
    {
        // Start watching inbox for this session
        _inboxWatcher.WatchSession(sessionId);

        return new SessionWorkspaceViewModel(
            sessionId,
            _sessionManager,
            _artifactStore,
            _snapshotService,
            _ocrService,
            _indexService,
            _retrievalService,
            _researchJobRunner,
            _discoveryJobRunner,
            _programmingJobRunner,
            _materialsJobRunner,
            _fusionJobRunner,
            _exportService,
            _llmService,
            _settings,
            _dialogService,
            _crossSearch,
            _citationVerifier,
            _contradictionDetector,
            _comparisonService,
            _repoRunner,
            _projectFusionEngine,
            _globalMemory,
            _notificationService,
            _discoveryService);
    }

    public WelcomeViewModel CreateWelcome()
    {
        return new WelcomeViewModel();
    }

    public SettingsViewModel CreateSettings()
    {
        return new SettingsViewModel(_settings, _llmService, _keyStore, _codexCli);
    }
}
