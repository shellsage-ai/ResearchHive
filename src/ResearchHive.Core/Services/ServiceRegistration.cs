using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<LlmService>(sp =>
            new LlmService(sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<CodexCliService>()));

        services.AddSingleton<ArtifactStore>(sp =>
            new ArtifactStore(settings.SessionsPath, sp.GetRequiredService<SessionManager>()));

        services.AddSingleton<SnapshotService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<PdfIngestionService>();
        services.AddSingleton<IndexService>();
        services.AddSingleton<RetrievalService>();
        services.AddSingleton<BrowserSearchService>(sp =>
            new BrowserSearchService(sp.GetRequiredService<AppSettings>()));
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
        services.AddSingleton<ComplementResearchService>();

        // Repo RAG services
        services.AddSingleton<RepoCloneService>();
        services.AddSingleton<CodeChunker>();
        services.AddSingleton<RepoIndexService>();
        services.AddSingleton<CodeBookGenerator>();

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
                sp.GetRequiredService<LlmService>(),
                sp.GetRequiredService<RepoIndexService>(),
                sp.GetRequiredService<CodeBookGenerator>(),
                sp.GetRequiredService<RetrievalService>());
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
