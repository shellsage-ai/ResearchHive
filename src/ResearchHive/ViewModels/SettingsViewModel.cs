using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;

namespace ResearchHive.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly LlmService _llmService;
    private readonly SecureKeyStore _keyStore;
    private readonly DispatcherTimer _autoSaveDebounce;

    // ── Core Settings ──
    [ObservableProperty] private string _dataRootPath;
    [ObservableProperty] private string _ollamaBaseUrl;
    [ObservableProperty] private string _embeddingModel;
    [ObservableProperty] private string _synthesisModel;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private string _ollamaStatus = "Checking...";

    // ── Routing Strategy ──
    [ObservableProperty] private RoutingStrategy _selectedRouting;

    // ── Cloud Provider ──
    [ObservableProperty] private bool _usePaidProvider;
    [ObservableProperty] private PaidProviderType _selectedPaidProvider;
    [ObservableProperty] private string _paidProviderApiKey = "";
    [ObservableProperty] private string _paidProviderEndpoint = "";
    [ObservableProperty] private string _selectedCloudModel = "";
    [ObservableProperty] private ApiKeySource _selectedKeySource;
    [ObservableProperty] private string _keyEnvironmentVariable = "";
    [ObservableProperty] private string _keyValidationStatus = "";
    [ObservableProperty] private bool _isValidatingKey;

    // ── GitHub Models ──
    [ObservableProperty] private string _gitHubPat = "";
    [ObservableProperty] private string _gitHubStatus = "";

    // ── Codex CLI Status ──
    [ObservableProperty] private string _codexAuthStatus = "";
    [ObservableProperty] private bool _codexIsReady;
    [ObservableProperty] private ChatGptPlusAuthMode _selectedChatGptPlusAuth;

    /// <summary>True when ChatGptPlus is selected AND auth mode is CodexOAuth.</summary>
    public bool ShowCodexOAuthSection => SelectedPaidProvider == PaidProviderType.ChatGptPlus
                                         && SelectedChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth;
    /// <summary>True when the API key / key-source fields should be visible.</summary>
    public bool ShowApiKeySection => SelectedPaidProvider != PaidProviderType.ChatGptPlus
                                     || SelectedChatGptPlusAuth == ChatGptPlusAuthMode.ApiKey;
    /// <summary>True when ChatGptPlus is the selected provider (controls auth mode picker visibility).</summary>
    public bool IsChatGptPlusSelected => SelectedPaidProvider == PaidProviderType.ChatGptPlus;

    public Array ChatGptPlusAuthModes => Enum.GetValues(typeof(ChatGptPlusAuthMode));

    // ── Tool Calling ──
    [ObservableProperty] private bool _enableToolCalling;
    [ObservableProperty] private int _maxToolCallsPerPhase;

    // ── Polite Browsing ──
    [ObservableProperty] private int _maxConcurrentFetches;
    [ObservableProperty] private double _minDomainDelay;
    [ObservableProperty] private double _maxDomainDelay;

    // ── Collections ──
    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<string> AvailableCloudModels { get; } = new();
    public Array PaidProviderTypes => Enum.GetValues(typeof(PaidProviderType));
    public Array RoutingStrategies => Enum.GetValues(typeof(RoutingStrategy));
    public Array ApiKeySources => Enum.GetValues(typeof(ApiKeySource));

    /// <summary>Unicode icon + label for the selected provider (used in UI).</summary>
    public string ProviderDisplayName => SelectedPaidProvider switch
    {
        PaidProviderType.OpenAI => "◆ OpenAI",
        PaidProviderType.Anthropic => "▲ Anthropic",
        PaidProviderType.GoogleGemini => "◉ Google Gemini",
        PaidProviderType.MistralAI => "◈ Mistral AI",
        PaidProviderType.OpenRouter => "⊕ OpenRouter",
        PaidProviderType.AzureOpenAI => "☁ Azure OpenAI",
        PaidProviderType.GitHubModels => "⬡ GitHub Models (Free)",
        PaidProviderType.ChatGptPlus => "⭐ ChatGPT Plus (Codex CLI)",
        _ => "None"
    };

    private readonly CodexCliService _codexCli;

    public SettingsViewModel(AppSettings settings, LlmService llmService, SecureKeyStore keyStore, CodexCliService codexCli)
    {
        _settings = settings;
        _llmService = llmService;
        _keyStore = keyStore;
        _codexCli = codexCli;

        // Load values
        _dataRootPath = settings.DataRootPath;
        _ollamaBaseUrl = settings.OllamaBaseUrl;
        _embeddingModel = settings.EmbeddingModel;
        _synthesisModel = settings.SynthesisModel;
        _selectedRouting = settings.Routing;
        _usePaidProvider = settings.UsePaidProvider;
        _selectedPaidProvider = settings.PaidProvider;
        _paidProviderEndpoint = settings.PaidProviderEndpoint ?? "";
        _selectedCloudModel = settings.PaidProviderModel ?? "";
        _selectedKeySource = settings.KeySource;
        _keyEnvironmentVariable = settings.KeyEnvironmentVariable ?? "";
        _selectedChatGptPlusAuth = settings.ChatGptPlusAuth;
        _enableToolCalling = settings.EnableToolCalling;
        _maxToolCallsPerPhase = settings.MaxToolCallsPerPhase;
        _maxConcurrentFetches = settings.MaxConcurrentFetches;
        _minDomainDelay = settings.MinDomainDelaySeconds;
        _maxDomainDelay = settings.MaxDomainDelaySeconds;

        // Load secure keys
        _paidProviderApiKey = _keyStore.LoadKey($"provider_{_selectedPaidProvider}") ?? settings.PaidProviderApiKey ?? "";
        _gitHubPat = _keyStore.LoadKey("github_pat") ?? settings.GitHubPat ?? "";

        // Debounced auto-save: 3 seconds after last change
        _autoSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoSaveDebounce.Tick += (_, _) =>
        {
            _autoSaveDebounce.Stop();
            Save();
        };

        // Populate cloud models for current provider
        PopulateCloudModels();
        _ = LoadModelsAsync();

        // Check Codex CLI status on load
        RefreshCodexStatus();
    }

    #region Change handlers

    partial void OnDataRootPathChanged(string value) => ScheduleAutoSave();
    partial void OnEmbeddingModelChanged(string value) => ScheduleAutoSave();
    partial void OnSynthesisModelChanged(string value) => ScheduleAutoSave();
    partial void OnUsePaidProviderChanged(bool value) => ScheduleAutoSave();
    partial void OnPaidProviderEndpointChanged(string value) => ScheduleAutoSave();
    partial void OnMaxConcurrentFetchesChanged(int value) => ScheduleAutoSave();
    partial void OnMinDomainDelayChanged(double value) => ScheduleAutoSave();
    partial void OnMaxDomainDelayChanged(double value) => ScheduleAutoSave();
    partial void OnSelectedRoutingChanged(RoutingStrategy value) => ScheduleAutoSave();
    partial void OnEnableToolCallingChanged(bool value) => ScheduleAutoSave();
    partial void OnMaxToolCallsPerPhaseChanged(int value) => ScheduleAutoSave();
    partial void OnKeyEnvironmentVariableChanged(string value) => ScheduleAutoSave();

    partial void OnSelectedKeySourceChanged(ApiKeySource value) => ScheduleAutoSave();

    partial void OnSelectedChatGptPlusAuthChanged(ChatGptPlusAuthMode value)
    {
        OnPropertyChanged(nameof(ShowCodexOAuthSection));
        OnPropertyChanged(nameof(ShowApiKeySection));
        RefreshCodexStatus();
        ScheduleAutoSave();
    }

    partial void OnSelectedCloudModelChanged(string value) => ScheduleAutoSave();

    partial void OnPaidProviderApiKeyChanged(string value)
    {
        // Encrypt and store the key immediately
        if (!string.IsNullOrWhiteSpace(value))
            _keyStore.SaveKey($"provider_{SelectedPaidProvider}", value);
        ScheduleAutoSave();
    }

    partial void OnGitHubPatChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _keyStore.SaveKey("github_pat", value);
        ScheduleAutoSave();
    }

    partial void OnSelectedPaidProviderChanged(PaidProviderType value)
    {
        // Auto-fill endpoint
        PaidProviderEndpoint = value switch
        {
            PaidProviderType.OpenAI => "https://api.openai.com/v1",
            PaidProviderType.Anthropic => "https://api.anthropic.com/v1",
            PaidProviderType.GoogleGemini => "https://generativelanguage.googleapis.com/v1beta",
            PaidProviderType.MistralAI => "https://api.mistral.ai/v1",
            PaidProviderType.OpenRouter => "https://openrouter.ai/api/v1",
            PaidProviderType.GitHubModels => "https://models.github.ai/inference",
            PaidProviderType.ChatGptPlus => SelectedChatGptPlusAuth == ChatGptPlusAuthMode.ApiKey
                ? "https://api.openai.com/v1" : "",
            PaidProviderType.AzureOpenAI => PaidProviderEndpoint,
            _ => ""
        };

        // Load the API key for this provider from secure store
        PaidProviderApiKey = _keyStore.LoadKey($"provider_{value}") ?? "";

        // Populate known models
        PopulateCloudModels();
        OnPropertyChanged(nameof(ProviderDisplayName));
        KeyValidationStatus = "";

        // Refresh visibility helpers
        OnPropertyChanged(nameof(IsChatGptPlusSelected));
        OnPropertyChanged(nameof(ShowApiKeySection));
        OnPropertyChanged(nameof(ShowCodexOAuthSection));

        // Refresh Codex status when switching to/from ChatGptPlus
        RefreshCodexStatus();
    }

    partial void OnOllamaBaseUrlChanged(string value)
    {
        OllamaStatus = "Checking...";
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task RefreshModels()
    {
        await LoadModelsAsync();
    }

    [RelayCommand]
    private async Task ValidateKey()
    {
        if (SelectedPaidProvider == PaidProviderType.None)
        {
            KeyValidationStatus = "Select a provider first.";
            return;
        }

        var key = SelectedPaidProvider == PaidProviderType.GitHubModels ? GitHubPat : PaidProviderApiKey;
        // ChatGPT Plus with CodexOAuth — no API key needed
        if (SelectedPaidProvider == PaidProviderType.ChatGptPlus
            && SelectedChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth)
        {
            key = "codex-oauth"; // sentinel — actual auth handled by CodexCliService
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            KeyValidationStatus = "Enter an API key first.";
            return;
        }

        IsValidatingKey = true;
        KeyValidationStatus = "Validating...";
        try
        {
            var (valid, message) = await _llmService.ValidateApiKeyAsync(SelectedPaidProvider, key);
            KeyValidationStatus = valid ? $"✓ {message}" : $"✗ {message}";
        }
        catch (Exception ex)
        {
            KeyValidationStatus = $"✗ Error: {ex.Message}";
        }
        IsValidatingKey = false;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.DataRootPath = DataRootPath;
        _settings.OllamaBaseUrl = OllamaBaseUrl;
        _settings.EmbeddingModel = EmbeddingModel;
        _settings.SynthesisModel = SynthesisModel;
        _settings.Routing = SelectedRouting;
        _settings.UsePaidProvider = UsePaidProvider;
        _settings.PaidProvider = SelectedPaidProvider;
        _settings.PaidProviderEndpoint = string.IsNullOrWhiteSpace(PaidProviderEndpoint) ? null : PaidProviderEndpoint;
        _settings.PaidProviderModel = string.IsNullOrWhiteSpace(SelectedCloudModel) ? null : SelectedCloudModel;
        _settings.KeySource = SelectedKeySource;
        _settings.KeyEnvironmentVariable = string.IsNullOrWhiteSpace(KeyEnvironmentVariable) ? null : KeyEnvironmentVariable;
        _settings.ChatGptPlusAuth = SelectedChatGptPlusAuth;
        _settings.EnableToolCalling = EnableToolCalling;
        _settings.MaxToolCallsPerPhase = MaxToolCallsPerPhase;
        _settings.MaxConcurrentFetches = MaxConcurrentFetches;
        _settings.MinDomainDelaySeconds = MinDomainDelay;
        _settings.MaxDomainDelaySeconds = MaxDomainDelay;

        // API keys are stored encrypted via SecureKeyStore, but we also keep a reference in settings
        // for the current session (not written to disk in plaintext)
        _settings.PaidProviderApiKey = string.IsNullOrWhiteSpace(PaidProviderApiKey) ? null : PaidProviderApiKey;
        _settings.GitHubPat = string.IsNullOrWhiteSpace(GitHubPat) ? null : GitHubPat;

        // Save settings (without raw API keys in the file)
        var configPath = Path.Combine(_settings.DataRootPath, "appsettings.json");
        Directory.CreateDirectory(_settings.DataRootPath);

        // Create a copy without sensitive keys for file storage
        var fileSafe = new AppSettings
        {
            DataRootPath = _settings.DataRootPath,
            OllamaBaseUrl = _settings.OllamaBaseUrl,
            EmbeddingModel = _settings.EmbeddingModel,
            SynthesisModel = _settings.SynthesisModel,
            Routing = _settings.Routing,
            UsePaidProvider = _settings.UsePaidProvider,
            PaidProvider = _settings.PaidProvider,
            PaidProviderEndpoint = _settings.PaidProviderEndpoint,
            PaidProviderModel = _settings.PaidProviderModel,
            KeySource = _settings.KeySource,
            KeyEnvironmentVariable = _settings.KeyEnvironmentVariable,
            ChatGptPlusAuth = _settings.ChatGptPlusAuth,
            EnableToolCalling = _settings.EnableToolCalling,
            MaxToolCallsPerPhase = _settings.MaxToolCallsPerPhase,
            MaxConcurrentFetches = _settings.MaxConcurrentFetches,
            MaxConcurrentPerDomain = _settings.MaxConcurrentPerDomain,
            MaxBrowserContexts = _settings.MaxBrowserContexts,
            MinDomainDelaySeconds = _settings.MinDomainDelaySeconds,
            MaxDomainDelaySeconds = _settings.MaxDomainDelaySeconds,
            // API keys intentionally omitted — stored encrypted in SecureKeyStore
        };

        var json = System.Text.Json.JsonSerializer.Serialize(fileSafe,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);

        StatusMessage = "Settings saved successfully.";
    }

    #endregion

    #region Helpers

    private void ScheduleAutoSave()
    {
        _autoSaveDebounce.Stop();
        _autoSaveDebounce.Start();
        StatusMessage = "Auto-saving…";
    }

    private void RefreshCodexStatus()
    {
        if (SelectedPaidProvider != PaidProviderType.ChatGptPlus)
        {
            CodexAuthStatus = "";
            CodexIsReady = false;
            return;
        }

        bool available = _codexCli.IsAvailable;
        bool authenticated = available && _codexCli.IsAuthenticated;

        if (!available)
        {
            CodexAuthStatus = "\u2717 Codex CLI not found. Run: npm i -g @openai/codex";
            CodexIsReady = false;
        }
        else if (!authenticated)
        {
            CodexAuthStatus = "\u2717 Not authenticated. Run: codex login";
            CodexIsReady = false;
        }
        else
        {
            CodexAuthStatus = "\u2713 Codex CLI installed & authenticated";
            CodexIsReady = true;
        }
    }

    private void PopulateCloudModels()
    {
        AvailableCloudModels.Clear();
        if (AppSettings.KnownCloudModels.TryGetValue(SelectedPaidProvider, out var models))
        {
            foreach (var m in models)
                AvailableCloudModels.Add(m);
        }

        // If the saved model isn't in the list, add it
        if (!string.IsNullOrEmpty(SelectedCloudModel) && !AvailableCloudModels.Contains(SelectedCloudModel))
            AvailableCloudModels.Insert(0, SelectedCloudModel);

        // Auto-select first model if none selected
        if (string.IsNullOrEmpty(SelectedCloudModel) && AvailableCloudModels.Count > 0)
            SelectedCloudModel = AvailableCloudModels[0];
    }

    private async Task LoadModelsAsync()
    {
        IsLoadingModels = true;
        OllamaStatus = "Connecting...";
        try
        {
            var models = await _llmService.ListModelsAsync();
            AvailableModels.Clear();
            if (models.Count > 0)
            {
                foreach (var m in models)
                    AvailableModels.Add(m);
                OllamaStatus = $"Connected — {models.Count} model(s) available";
            }
            else
            {
                OllamaStatus = "⚠ No models found. Pull models with: ollama pull <model>";
            }
        }
        catch
        {
            AvailableModels.Clear();
            OllamaStatus = "⚠ Cannot reach Ollama. Is it running?";
        }
        IsLoadingModels = false;
    }

    #endregion
}
