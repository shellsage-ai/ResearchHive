using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using System.Collections.ObjectModel;

namespace ResearchHive.ViewModels;

public partial class SessionsSidebarViewModel : ObservableObject
{
    private readonly SessionManager _sessionManager;
    private readonly ViewModelFactory _factory;
    private readonly Action<string> _onSessionSelected;
    private readonly Action<string>? _onSessionDeleted;
    private readonly IDialogService _dialogService;

    [ObservableProperty] private ObservableCollection<SessionItemViewModel> _sessions = new();
    [ObservableProperty] private SessionItemViewModel? _selectedSession;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _filterTag = string.Empty;
    [ObservableProperty] private bool _isCreatingSession;
    [ObservableProperty] private string _newTitle = string.Empty;
    [ObservableProperty] private string _newDescription = string.Empty;
    [ObservableProperty] private DomainPack _newPack = DomainPack.GeneralResearch;
    [ObservableProperty] private string _newTags = string.Empty;

    public Array DomainPacks => Enum.GetValues(typeof(DomainPack));

    public SessionsSidebarViewModel(
        SessionManager sessionManager, 
        ViewModelFactory factory, 
        Action<string> onSessionSelected,
        IDialogService dialogService,
        Action<string>? onSessionDeleted = null)
    {
        _sessionManager = sessionManager;
        _factory = factory;
        _onSessionSelected = onSessionSelected;
        _dialogService = dialogService;
        _onSessionDeleted = onSessionDeleted;
        LoadSessions();
    }

    public void LoadSessions()
    {
        var sessions = string.IsNullOrWhiteSpace(SearchText)
            ? _sessionManager.GetAllSessions()
            : _sessionManager.SearchSessions(SearchText);

        if (!string.IsNullOrWhiteSpace(FilterTag))
        {
            sessions = sessions.Where(s => s.Tags.Any(t =>
                t.Contains(FilterTag, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        Sessions.Clear();
        foreach (var s in sessions)
        {
            Sessions.Add(new SessionItemViewModel(s));
        }
    }

    partial void OnSearchTextChanged(string value) => LoadSessions();
    partial void OnFilterTagChanged(string value) => LoadSessions();

    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        if (value != null)
            _onSessionSelected(value.Session.Id);
    }

    [RelayCommand]
    private void StartCreateSession()
    {
        IsCreatingSession = true;
        NewTitle = "";
        NewDescription = "";
        NewPack = DomainPack.GeneralResearch;
        NewTags = "";
    }

    [RelayCommand]
    private void ConfirmCreateSession()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) return;

        var tags = NewTags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).ToList();

        var session = _sessionManager.CreateSession(NewTitle, NewDescription, NewPack, tags);
        IsCreatingSession = false;
        LoadSessions();

        // Auto-select the new session
        var item = Sessions.FirstOrDefault(s => s.Session.Id == session.Id);
        if (item != null) SelectedSession = item;
    }

    [RelayCommand]
    private void CancelCreateSession()
    {
        IsCreatingSession = false;
    }

    [RelayCommand]
    private void DeleteSession(SessionItemViewModel? item)
    {
        if (item == null) return;
        if (!_dialogService.Confirm(
            $"Delete session \"{item.Title}\"?\n\nThis will permanently remove all snapshots, reports, evidence, and artifacts for this session.",
            "Delete Session"))
            return;

        var deletedId = item.Session.Id;
        _sessionManager.CloseSessionDb(deletedId);
        _sessionManager.DeleteSession(deletedId);
        LoadSessions();
        _onSessionDeleted?.Invoke(deletedId);
    }

    [RelayCommand]
    private void RefreshSessions() => LoadSessions();
}

public partial class SessionItemViewModel : ObservableObject
{
    public Session Session { get; }

    public string Title => Session.Title;
    public string Description => Session.Description;
    public string Pack => Session.Pack.ToDisplayName();
    public string Status => Session.Status.ToDisplayName();
    public string Tags => string.Join(", ", Session.Tags);
    public string Created => Session.CreatedUtc.ToLocalTime().ToString("g");
    public string Updated => Session.UpdatedUtc.ToLocalTime().ToString("g");
    public string? LastReport => Session.LastReportSummary;
    public string StatusColor => Session.Status switch
    {
        SessionStatus.Active => "#4CAF50",
        SessionStatus.Paused => "#FF9800",
        SessionStatus.Completed => "#2196F3",
        SessionStatus.Archived => "#9E9E9E",
        _ => "#757575"
    };

    public SessionItemViewModel(Session session)
    {
        Session = session;
    }
}
