using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Services;
using System.Windows.Threading;

namespace ResearchHive.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ViewModelFactory _factory;
    private readonly LlmService _llmService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _ollamaTimer;

    [ObservableProperty] private ObservableObject? _currentView;
    [ObservableProperty] private SessionsSidebarViewModel _sidebar;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _currentTabName = "Overview";
    [ObservableProperty] private bool _isOllamaConnected;
    [ObservableProperty] private string _ollamaStatus = "Checking Ollama…";

    public MainViewModel(SessionManager sessionManager, ViewModelFactory factory, LlmService llmService, AppSettings settings)
    {
        _factory = factory;
        _llmService = llmService;
        _settings = settings;
        _sidebar = factory.CreateSessionsSidebar(OnSessionSelected, OnSessionDeleted);
        _currentView = factory.CreateWelcome();

        // Check Ollama status immediately and every 30 seconds
        _ollamaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ollamaTimer.Tick += async (_, _) => await CheckOllamaAsync();
        _ollamaTimer.Start();
        _ = CheckOllamaAsync();
    }

    private async Task CheckOllamaAsync()
    {
        // When routing is CloudOnly, Ollama is irrelevant — hide the banner
        if (_settings.Routing == RoutingStrategy.CloudOnly)
        {
            IsOllamaConnected = true;
            OllamaStatus = $"Using cloud AI ({_settings.PaidProvider})";
            return;
        }

        try
        {
            var available = await _llmService.CheckAvailabilityAsync();
            IsOllamaConnected = available;
            OllamaStatus = available ? "Ollama connected" : "Ollama offline — using fallback mode";
        }
        catch
        {
            IsOllamaConnected = false;
            OllamaStatus = "Ollama offline — using fallback mode";
        }
    }

    private void OnSessionSelected(string sessionId)
    {
        _activeSessionId = sessionId;
        CurrentView = _factory.CreateSessionWorkspace(sessionId);
        StatusText = $"Session loaded: {sessionId}";
    }

    private string? _activeSessionId;

    private void OnSessionDeleted(string sessionId)
    {
        if (_activeSessionId == sessionId)
        {
            _activeSessionId = null;
            CurrentView = _factory.CreateWelcome();
            StatusText = "Session deleted";
        }
    }

    [RelayCommand]
    private void NavigateToTab(string tabName)
    {
        CurrentTabName = tabName;
        if (CurrentView is SessionWorkspaceViewModel workspace)
        {
            workspace.ActiveTab = tabName;
        }
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CurrentView = _factory.CreateSettings();
        StatusText = "Settings";
    }

    [RelayCommand]
    private void ShowHome()
    {
        CurrentView = _factory.CreateWelcome();
        StatusText = "Ready";
    }
}
