namespace ResearchHive.Core.Configuration;

/// <summary>Which model tier to use for an LLM call. Enables intelligent routing of routine tasks to cheaper/faster models.</summary>
public enum ModelTier
{
    /// <summary>Use the user's selected model (default behavior).</summary>
    Default,
    /// <summary>Use a smaller/cheaper model for routine tasks (CodeBook, gap verify, complement eval).</summary>
    Mini,
    /// <summary>Use the most capable model. Reserved for complex agentic tasks.</summary>
    Full
}

public enum PaidProviderType
{
    None,
    OpenAI,
    Anthropic,
    GoogleGemini,
    MistralAI,
    OpenRouter,
    AzureOpenAI,
    GitHubModels,
    /// <summary>ChatGPT Plus via Codex CLI — authenticates via OAuth, no API key needed.</summary>
    ChatGptPlus
}

/// <summary>How the system routes LLM calls between local and cloud.</summary>
public enum RoutingStrategy
{
    /// <summary>Only use local Ollama — never call cloud.</summary>
    LocalOnly,
    /// <summary>Try Ollama first, fall back to cloud on failure (default).</summary>
    LocalWithCloudFallback,
    /// <summary>Use cloud as primary, Ollama as fallback.</summary>
    CloudPrimary,
    /// <summary>Only use cloud provider — skip Ollama entirely.</summary>
    CloudOnly
}

/// <summary>How the API key is sourced for cloud providers.</summary>
public enum ApiKeySource
{
    /// <summary>Key entered directly in settings (DPAPI-encrypted at rest).</summary>
    Direct,
    /// <summary>Key read from an environment variable.</summary>
    EnvironmentVariable
}

/// <summary>Authentication mode for ChatGPT Plus provider.</summary>
public enum ChatGptPlusAuthMode
{
    /// <summary>Authenticate via Codex CLI OAuth (ChatGPT Plus subscription login, no API key).</summary>
    CodexOAuth,
    /// <summary>Use a standard OpenAI API key (pay-per-token, same models).</summary>
    ApiKey
}

public class AppSettings
{
    public string DataRootPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResearchHive");
    public string SessionsPath => Path.Combine(DataRootPath, "Sessions");
    public string RegistryDbPath => Path.Combine(DataRootPath, "registry.db");

    // Model settings
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string SynthesisModel { get; set; } = "llama3.1:8b";

    // Routing strategy
    public RoutingStrategy Routing { get; set; } = RoutingStrategy.LocalWithCloudFallback;

    // Cloud provider settings
    public bool UsePaidProvider { get; set; } = false;
    public PaidProviderType PaidProvider { get; set; } = PaidProviderType.None;
    public string? PaidProviderApiKey { get; set; }
    public string? PaidProviderEndpoint { get; set; }
    public string? PaidProviderModel { get; set; }  // User-selected cloud model

    // API key sourcing
    public ApiKeySource KeySource { get; set; } = ApiKeySource.Direct;
    public string? KeyEnvironmentVariable { get; set; }  // e.g. "OPENAI_API_KEY"

    // GitHub Models (free with GitHub account / Copilot subscription)
    public string? GitHubPat { get; set; }  // Personal Access Token with models:read scope

    // ChatGPT Plus via Codex CLI (v0.98+ Rust binary)
    // Legacy paths (used as fallback if native binary not found)
    public string CodexNodePath { get; set; } = @"C:\nvm4w\nodejs\node.EXE";
    public string CodexScriptPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
    public string CodexModel { get; set; } = "gpt-5.3-codex";
    /// <summary>Mini model for routine Codex tasks (CodeBook, gap verification, complement evaluation). 4x more efficient.</summary>
    public string CodexMiniModel { get; set; } = "gpt-5.1-codex-mini";
    /// <summary>Enable live web search for Codex CLI research prompts.</summary>
    public bool CodexEnableWebSearch { get; set; } = true;
    /// <summary>How to authenticate when provider is ChatGptPlus.</summary>
    public ChatGptPlusAuthMode ChatGptPlusAuth { get; set; } = ChatGptPlusAuthMode.CodexOAuth;

    /// <summary>
    /// When true AND the active provider is ChatGPT Plus (CodexOAuth), the pipeline skips
    /// orchestration calls (plan, decompose, coverage, deep search) and lets Codex do a single
    /// synthesis call with web search enabled. The model handles its own research autonomously.
    /// Ollama and other cloud API providers always use the full pipeline regardless of this flag.
    /// </summary>
    public bool StreamlinedCodexMode { get; set; } = true;

    /// <summary>
    /// When true, URLs are scored by domain authority (.edu, .gov, journals > blogs/forums).
    /// Default OFF — turn on when authoritative/academic sources are preferred.
    /// Informal topics (gardening, hobbies) should leave this off to get diverse results.
    /// </summary>
    public bool SourceQualityRanking { get; set; } = false;

    /// <summary>
    /// Time range filter for web searches. Appends date parameters to each search engine.
    /// Values: "any" (default), "year", "month", "week", "day".
    /// </summary>
    public string SearchTimeRange { get; set; } = "any";

    /// <summary>
    /// When true, Windows toast notifications are shown when long-running jobs complete.
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// When true, reports are generated section-by-section from a template rather than
    /// in a single LLM call. Produces longer, more detailed reports especially from local models.
    /// </summary>
    public bool SectionalReports { get; set; } = true;

    // Tool calling
    public bool EnableToolCalling { get; set; } = true;
    public int MaxToolCallsPerPhase { get; set; } = 10;

    // Browsing courtesy settings
    public int MaxConcurrentFetches { get; set; } = 6;
    public int MaxConcurrentPerDomain { get; set; } = 2;
    public int MaxBrowserContexts { get; set; } = 8;
    public int EmbeddingConcurrency { get; set; } = 4;
    public double MinDomainDelaySeconds { get; set; } = 1.5;
    public double MaxDomainDelaySeconds { get; set; } = 3.0;
    public int MaxRetries { get; set; } = 3;
    public double BackoffBaseSeconds { get; set; } = 2.0;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public string UserAgentString { get; set; } = "ResearchHive/1.0 (Windows; Academic Research Assistant)";

    // Search / retrieval
    public int DefaultChunkSize { get; set; } = 500;
    public int DefaultChunkOverlap { get; set; } = 50;
    public int DefaultTopK { get; set; } = 10;
    public float SemanticWeight { get; set; } = 0.5f;
    public float KeywordWeight { get; set; } = 0.5f;

    // LLM context window
    /// <summary>Context window size sent to Ollama via num_ctx. Increase if using a model with larger context.</summary>
    public int LocalContextSize { get; set; } = 16384;

    // Repo RAG settings
    /// <summary>Chunk size (words) for code/doc files during repo indexing.</summary>
    public int RepoChunkSize { get; set; } = 800;
    /// <summary>Overlap (words) between code/doc chunks.</summary>
    public int RepoChunkOverlap { get; set; } = 100;
    /// <summary>Maximum number of files to index per repository.</summary>
    public int RepoMaxFiles { get; set; } = 200;
    /// <summary>Skip files larger than this (bytes). Default 150 KB.</summary>
    public int RepoMaxFileSizeBytes { get; set; } = 150_000;

    // Hive Mind / Global memory
    /// <summary>Path to the global memory database shared across all sessions.</summary>
    public string GlobalDbPath => Path.Combine(DataRootPath, "global.db");
    /// <summary>Path for shallow repo clones used by Repo RAG.</summary>
    public string RepoClonePath => Path.Combine(DataRootPath, "repos");

    /// <summary>
    /// Known model lists per provider for dropdown population.
    /// </summary>
    public static IReadOnlyDictionary<PaidProviderType, string[]> KnownCloudModels { get; } = new Dictionary<PaidProviderType, string[]>
    {
        [PaidProviderType.OpenAI] = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "o3", "o3-mini", "o4-mini" },
        [PaidProviderType.Anthropic] = new[] { "claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-haiku-4-20250514" },
        [PaidProviderType.GoogleGemini] = new[] { "gemini-2.0-flash", "gemini-2.0-pro", "gemini-1.5-pro", "gemini-1.5-flash" },
        [PaidProviderType.MistralAI] = new[] { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest", "codestral-latest" },
        [PaidProviderType.OpenRouter] = new[] { "openai/gpt-4o", "anthropic/claude-sonnet-4-20250514", "google/gemini-2.0-flash", "meta-llama/llama-3.1-405b-instruct" },
        [PaidProviderType.AzureOpenAI] = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1" },
        [PaidProviderType.GitHubModels] = new[] { "openai/gpt-4o", "openai/gpt-4o-mini", "openai/gpt-4.1", "openai/gpt-4.1-mini", "openai/o3-mini", "openai/o4-mini" },
        [PaidProviderType.ChatGptPlus] = new[] { "gpt-5.3-codex", "gpt-5.1-codex-mini", "o3", "o4-mini", "gpt-4.1", "gpt-4o" },
    };

    /// <summary>
    /// Maps each provider to its best "mini" model for routine tasks.
    /// Used when <see cref="ModelTier.Mini"/> is requested.
    /// </summary>
    public static IReadOnlyDictionary<PaidProviderType, string> MiniModelMap { get; } = new Dictionary<PaidProviderType, string>
    {
        [PaidProviderType.OpenAI] = "gpt-4.1-mini",
        [PaidProviderType.Anthropic] = "claude-haiku-4-20250514",
        [PaidProviderType.GoogleGemini] = "gemini-2.0-flash",
        [PaidProviderType.MistralAI] = "mistral-small-latest",
        [PaidProviderType.OpenRouter] = "openai/gpt-4o-mini",
        [PaidProviderType.AzureOpenAI] = "gpt-4o-mini",
        [PaidProviderType.GitHubModels] = "openai/gpt-4o-mini",
        [PaidProviderType.ChatGptPlus] = "gpt-5.1-codex-mini",
    };
}
