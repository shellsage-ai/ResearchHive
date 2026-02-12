using Microsoft.Extensions.DependencyInjection;
using ResearchHive.Core.Configuration;
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
        services.AddSingleton<IndexService>();
        services.AddSingleton<RetrievalService>();
        services.AddSingleton<BrowserSearchService>(sp =>
            new BrowserSearchService(sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<GoogleSearchService>();
        services.AddSingleton<ResearchJobRunner>();
        services.AddSingleton<DiscoveryJobRunner>();
        services.AddSingleton<ProgrammingJobRunner>();
        services.AddSingleton<MaterialsJobRunner>();
        services.AddSingleton<FusionJobRunner>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<InboxWatcher>();
        services.AddSingleton<CrossSessionSearchService>();
        services.AddSingleton<CitationVerificationService>();
        services.AddSingleton<ContradictionDetector>();
        services.AddSingleton<ResearchComparisonService>();

        return services;
    }
}
