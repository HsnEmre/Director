using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows;
using Director.Data;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.ViewModels;
using Director.Views;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director;

public partial class App : Application
{
    private IHost? _host;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureAppConfiguration((context, configuration) =>
            {
                configuration.SetBasePath(AppContext.BaseDirectory);
                configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureLogging(logging =>
            {
                logging.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));
            })
            .ConfigureServices((context, services) =>
            {
                var connectionString = context.Configuration.GetConnectionString("DefaultConnection");

                services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseSqlServer(connectionString));

                services.Configure<OllamaOptions>(context.Configuration.GetSection("Ollama"));
                services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();
                services.Configure<WanGpOptions>(context.Configuration.GetSection("WanGp"));
                services.AddSingleton<IValidateOptions<WanGpOptions>, WanGpOptionsValidator>();
                services.AddHttpClient<IOllamaClient, OllamaClient>()
                    .ConfigurePrimaryHttpMessageHandler(serviceProvider => new SocketsHttpHandler
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(Math.Max(
                            1,
                            serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value.SceneConnectTimeoutSeconds))
                    });
                services.AddSingleton<IFilmProjectService, FilmProjectService>();
                services.AddSingleton<IStoryGenerationService, StoryGenerationService>();
                services.AddSingleton<IStoryPromptBuilder, StoryPromptBuilder>();
                services.AddSingleton<IOllamaFailureDiagnosticWriter, OllamaFailureDiagnosticWriter>();
                services.AddSingleton<IWanGpClient, WanGpMcpClient>();
                services.AddSingleton<IWanGpProcessManager, WanGpProcessManager>();
                services.AddSingleton<IWanGpRuntimeCoordinator, WanGpRuntimeCoordinator>();
                services.AddSingleton<IWanGpLocalModelInventoryService, WanGpLocalModelInventoryService>();
                services.AddSingleton<IWanGpOutputResolver, WanGpOutputResolver>();
                services.AddSingleton<IWanGpVideoOutputResolver, WanGpVideoOutputResolver>();
                services.AddSingleton<IGpuGenerationCoordinator, GpuGenerationCoordinator>();
                services.AddSingleton<IApplicationActivityCenter, ApplicationActivityCenter>();
                services.AddHttpClient<IOllamaModelLifecycleService, OllamaModelLifecycleService>();
                services.AddSingleton<IMediaFileService, MediaFileService>();
                services.AddSingleton<IImageThumbnailService, ImageThumbnailService>();
                services.AddSingleton<IVideoMetadataService, VideoMetadataService>();
                services.AddSingleton<IWanGpVideoInputContractResolver, WanGpVideoInputContractResolver>();
                services.AddSingleton<IWanGpVideoTimingContractResolver, WanGpVideoTimingContractResolver>();
                services.AddSingleton<IWanGpVideoRequestBuilder, WanGpVideoRequestBuilder>();
                services.AddSingleton<IWanGpAudioInputContractResolver, WanGpAudioInputContractResolver>();
                services.AddSingleton<IWanGpAudioRequestBuilder, WanGpAudioRequestBuilder>();
                services.AddSingleton<IWanGpAudioOutputResolver, WanGpAudioOutputResolver>();
                services.AddSingleton<ISpeechTimelineMixingService, FfmpegSpeechTimelineMixingService>();
                services.AddSingleton<IFinalDialogueVideoMuxingService, FfmpegFinalDialogueVideoMuxingService>();
                services.AddSingleton<IVideoPromptComposerService, VideoPromptComposerService>();
                services.AddSingleton<ILtxNativeDialoguePromptComposer, LtxNativeDialoguePromptComposer>();
                services.AddSingleton<ILtxNativeDialogueCapabilityResolver, LtxNativeDialogueCapabilityResolver>();
                services.AddSingleton<IFinalMovieAssemblyService, FfmpegFinalMovieAssemblyService>();
                services.AddSingleton<IImageGenerationService, ImageGenerationService>();
                services.AddSingleton<IVideoGenerationService, VideoGenerationService>();
                services.AddSingleton<IAudioGenerationService, AudioGenerationService>();
                services.AddSingleton<IProjectGenerationLeaseCoordinator, ProjectGenerationLeaseCoordinator>();
                services.AddSingleton<IMessageService, MessageService>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<CreateFilmProjectViewModel>();
                services.AddTransient<StoryGenerationViewModel>();
                services.AddTransient<ProjectHistoryViewModel>();
                services.AddTransient<AudioProductionViewModel>();
                services.AddTransient<ProductionWorkspaceViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        await CheckDatabaseConnectionAsync();
        await MarkInterruptedJobsAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            var runtimeCoordinator = _host.Services.GetService<IWanGpRuntimeCoordinator>();
            if (runtimeCoordinator is not null)
            {
                await runtimeCoordinator.StopOwnedProcessAsync();
            }

            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private async Task CheckDatabaseConnectionAsync()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            using var scope = _host.Services.CreateScope();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();

            if (!await dbContext.Database.CanConnectAsync())
            {
                ShowDatabaseWarning(configuration, "SQL Server bağlantısı kurulamadı.");
            }
        }
        catch (Exception ex)
        {
            using var scope = _host.Services.CreateScope();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var logger = scope.ServiceProvider.GetService<ILogger<App>>();
            logger?.LogError(ex, "Director database connection check failed.");
            ShowDatabaseWarning(configuration, "SQL Server baglantisi kurulamadi.");
        }
    }

    private async Task MarkInterruptedJobsAsync()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            var service = _host.Services.GetRequiredService<IImageGenerationService>();
            await service.MarkOrphanRunningJobsInterruptedAsync();
        }
        catch
        {
            // Database connectivity is reported separately; startup should remain usable.
        }
    }

    private static void ShowDatabaseWarning(IConfiguration configuration, string detail)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "DefaultConnection bulunamadı.";
        var connectionSummary = BuildSafeConnectionSummary(connectionString);
        MessageBox.Show(
            "Director veritabanına bağlanamadı. Uygulama açılacak, ancak kayıt işlemleri SQL Server bağlantısı düzeltilene kadar başarısız olabilir."
            + Environment.NewLine + Environment.NewLine
            + "Configuration: appsettings.json > ConnectionStrings:DefaultConnection"
            + Environment.NewLine
            + $"Provider: {connectionSummary.Provider}"
            + Environment.NewLine
            + $"Database: {connectionSummary.DatabaseName}"
            + Environment.NewLine + Environment.NewLine
            + $"Ayrıntı: {detail}",
            "Veritabanı Bağlantısı",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static SafeConnectionSummary BuildSafeConnectionSummary(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new SafeConnectionSummary("SqlServer", "(configured connection string not found)");
        }

        try
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };
            var databaseName = ReadConnectionValue(builder, "Database")
                ?? ReadConnectionValue(builder, "Initial Catalog")
                ?? ReadConnectionValue(builder, "AttachDbFilename")
                ?? "(unknown)";
            return new SafeConnectionSummary("SqlServer", Path.GetFileName(databaseName));
        }
        catch (ArgumentException)
        {
            return new SafeConnectionSummary("SqlServer", "(unreadable)");
        }
    }

    private static string? ReadConnectionValue(System.Data.Common.DbConnectionStringBuilder builder, string key) =>
        builder.TryGetValue(key, out var value) ? value?.ToString() : null;

    private sealed record SafeConnectionSummary(string Provider, string DatabaseName);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logger = _host?.Services.GetService<ILogger<App>>();
            var currentWindow = Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                ?? Current?.MainWindow;
            var currentView = FindCurrentView(currentWindow);
            logger?.LogError(
                e.Exception,
                "Unhandled dispatcher exception. Type={ExceptionType}; Message={Message}; Inner={InnerException}; CurrentView={CurrentView}; DataContext={DataContext}",
                e.Exception.GetType().FullName,
                e.Exception.Message,
                e.Exception.InnerException?.ToString(),
                currentView?.GetType().FullName ?? currentWindow?.GetType().FullName,
                (currentView as FrameworkElement)?.DataContext?.GetType().FullName ?? currentWindow?.DataContext?.GetType().FullName);
        }
        catch
        {
            // Exception logging must never replace the original dispatcher exception.
        }
    }

    private static DependencyObject? FindCurrentView(DependencyObject? root)
    {
        if (root is ContentControl { Content: FrameworkElement content })
        {
            return content;
        }

        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            var result = FindCurrentView(child);
            if (result is not null)
            {
                return result;
            }
        }

        return root;
    }
}
