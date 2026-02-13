using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Data;
using System.Runtime.Versioning;

namespace ResearchHive.Core.Services;

public static class ServiceRegistration
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddResearchHiveCore(this IServiceCollection services, AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        services.AddSingleton(settings);

        services.AddSingleton<SecureKeyStore>(sp => new SecureKeyStore(settings.DataRootPath));
        services.AddSingleton<SessionManager>();
        services.AddSingleton<CourtesyPolicy>();
        services.AddSingleton<EmbeddingService>();
        services.AddSingleton<CodexCliService>();

        // Circuit breaker for LLM provider fault isolation
        services.AddSingleton<LlmCircuitBreaker>(sp =>
            new LlmCircuitBreaker(logger: sp.GetService<ILogger<LlmCircuitBreaker>>()));

        // LlmService: registered as concrete + ILlmService (same singleton)
        services.AddSingleton<LlmService>(sp =>
            new LlmService(
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<CodexCliService>(),
                sp.GetService<ILogger<LlmService>>(),
                sp.GetService<LlmCircuitBreaker>()));
        services.AddSingleton<ILlmService>(sp => sp.GetRequiredService<LlmService>());

        services.AddSingleton<ArtifactStore>(sp =>
            new ArtifactStore(settings.SessionsPath, sp.GetRequiredService<SessionManager>()));

        services.AddSingleton<SnapshotService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<PdfIngestionService>();
        services.AddSingleton<IndexService>();
        services.AddSingleton<RetrievalService>();
        services.AddSingleton<IRetrievalService>(sp => sp.GetRequiredService<RetrievalService>());
        services.AddSingleton<BrowserSearchService>(sp =>
            new BrowserSearchService(sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<IBrowserSearchService>(sp => sp.GetRequiredService<BrowserSearchService>());
        services.AddSingleton<GoogleSearchService>();
        services.AddSingleton<ResearchJobRunner>(sp =>
        {
            var runner = new ResearchJobRunner(
                sp.GetRequiredService<SessionManager>(),
                sp.GetRequiredService<SnapshotService>(),
                sp.GetRequiredService<IndexService>(),
                sp.GetRequiredService<RetrievalService>(),
                sp.GetRequiredService<LlmService>(),
                sp.GetRequiredService<EmbeddingService>(),
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<BrowserSearchService>(),
                sp.GetRequiredService<GoogleSearchService>());
            runner.GlobalMemory = sp.GetRequiredService<GlobalMemoryService>();
            return runner;
        });
        services.AddSingleton<DiscoveryJobRunner>();
        services.AddSingleton<ProgrammingJobRunner>();
        services.AddSingleton<MaterialsJobRunner>();
        services.AddSingleton<FusionJobRunner>();
        services.AddSingleton<RepoScannerService>();
        services.AddSingleton<ComplementResearchService>(sp =>
            new ComplementResearchService(
                sp.GetRequiredService<IBrowserSearchService>(),
                sp.GetRequiredService<ILlmService>(),
                sp.GetRequiredService<AppSettings>()));

        // Repo RAG services
        services.AddSingleton<RepoCloneService>();
        services.AddSingleton<CodeChunker>();
        services.AddSingleton<RepoIndexService>();
        services.AddSingleton<CodeBookGenerator>(sp =>
            new CodeBookGenerator(
                sp.GetRequiredService<IRetrievalService>(),
                sp.GetRequiredService<ILlmService>()));

        // Deterministic fact sheet builder + post-scan verifier (zero-LLM ground truth)
        services.AddSingleton<RepoFactSheetBuilder>();
        services.AddSingleton<PostScanVerifier>(sp =>
            new PostScanVerifier(
                sp.GetService<ILogger<PostScanVerifier>>(),
                sp.GetRequiredService<ILlmService>()));

        // Global memory (Hive Mind)
        services.AddSingleton<GlobalDb>(sp => new GlobalDb(settings.GlobalDbPath));
        services.AddSingleton<GlobalMemoryService>();

        // Wire RepoIntelligenceJobRunner with optional new services
        services.AddSingleton<RepoIntelligenceJobRunner>(sp =>
        {
            var runner = new RepoIntelligenceJobRunner(
                sp.GetRequiredService<SessionManager>(),
                sp.GetRequiredService<RepoScannerService>(),
                sp.GetRequiredService<ComplementResearchService>(),
                sp.GetRequiredService<ILlmService>(),
                sp.GetRequiredService<RepoIndexService>(),
                sp.GetRequiredService<CodeBookGenerator>(),
                sp.GetRequiredService<IRetrievalService>(),
                sp.GetService<ILogger<RepoIntelligenceJobRunner>>(),
                sp.GetRequiredService<RepoFactSheetBuilder>(),
                sp.GetRequiredService<PostScanVerifier>(),
                sp.GetRequiredService<RepoCloneService>());
            runner.GlobalMemory = sp.GetRequiredService<GlobalMemoryService>();
            return runner;
        });

        services.AddSingleton<ProjectFusionEngine>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<InboxWatcher>();
        services.AddSingleton<CrossSessionSearchService>();
        services.AddSingleton<CitationVerificationService>();
        services.AddSingleton<ContradictionDetector>();
        services.AddSingleton<ResearchComparisonService>();

        return services;
    }
}
