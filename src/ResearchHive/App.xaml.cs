using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using ResearchHive.ViewModels;

namespace ResearchHive;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // Load or create settings
        var settings = LoadOrCreateSettings();

        // Register core services (this also registers settings)
        services.AddResearchHiveCore(settings);

        // Register dialog service
        services.AddSingleton<IDialogService, DialogService>();

        // Register notification service (Windows toast)
        services.AddSingleton<NotificationService>();

        // Register ViewModelFactory and ViewModels
        services.AddSingleton<ViewModelFactory>();
        services.AddTransient<MainViewModel>(sp =>
            sp.GetRequiredService<ViewModelFactory>().CreateMainViewModel());

        // Register MainWindow
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        // Show main window
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NotificationService.Cleanup();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static AppSettings LoadOrCreateSettings()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResearchHive");

        var configPath = Path.Combine(appDataPath, "appsettings.json");

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
            catch { }
        }

        // Create default settings
        var settings = new AppSettings
        {
            DataRootPath = appDataPath
        };

        Directory.CreateDirectory(appDataPath);
        var defaultJson = System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, defaultJson);

        return settings;
    }
}

