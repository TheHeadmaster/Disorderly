using System.IO;
using System.Windows;
using Disorderly.Configuration;
using Disorderly.Domain;
using Disorderly.Resolvers;
using Disorderly.Services;
using Disorderly.View;
using Disorderly.View.Windows;
using ReactiveUI;
using ReactiveUI.Builder;
using Serilog;
using Serilog.Formatting.Compact;

namespace Disorderly;

/// <summary>
/// The main entry point of the application.
/// </summary>
public partial class App : Application
{
    private CommandPaletteController? paletteController;
    private GlobalHotkeyService? hotkeyService;
    private ConfigurationService? configuration;

    /// <summary>
    /// The list of directories used by the application.
    /// </summary>
    private static List<string> Directories { get; } =
    [
        AppMeta.UserDirectory,
        AppMeta.AppDataDirectory,
        AppMeta.LogsDirectory
    ];

    /// <summary>
    /// Creates directories if they are not already created.
    /// </summary>
    private static void CreateDirectories()
    {
        foreach (string directory in Directories)
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Initializes the application.
    /// </summary>
    private static void Initialize()
    {
        CreateDirectories();

        InitializeLogging();

        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .WithViewsFromAssembly(typeof(App).Assembly)
            .WithRegistration(locator => locator.Register(() => new CustomPropertyResolver(), typeof(ICreatesObservableForProperty)))
            .WithMainThreadScheduler(RxSchedulers.MainThreadScheduler)
            .WithTaskPoolScheduler(RxSchedulers.TaskpoolScheduler)
            .BuildApp();
    }

    /// <summary>
    /// Initializes the logging manager and logs the welcome messages.
    /// </summary>
    private static void InitializeLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Debug()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(AppMeta.LogsDirectory, $"info.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("Welcome to Disorderly Version {Version}.", AppMeta.Version);

        Log.Information("This assembly is running in {Mode} mode.", AppMeta.IsDebug ? "DEBUG" : "RELEASE");
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);

        Initialize();
        this.configuration = new ConfigurationService(PaletteConfiguration.CreateEmptyStore(), Path.Combine(AppMeta.AppDataDirectory, "config.json"));
        this.paletteController = new CommandPaletteController(new CommandPaletteWindow(), this.configuration.Store);
        TrayIconHandler.Initialize(() => this.Dispatcher.BeginInvoke(new Action(() => this.paletteController.Open())),
            () => this.Dispatcher.BeginInvoke(new Action(async () => await this.ReloadConfigurationAsync())));
        this.hotkeyService = new GlobalHotkeyService(new WindowsHotkeyRegistration());
        this.hotkeyService.Activated += this.OnHotkeyActivated;
        this.hotkeyService.Start();
        await this.ReloadConfigurationAsync();
    }

    private async Task ReloadConfigurationAsync()
    {
        try
        {
            ConfigurationResult result = await this.configuration!.ReloadAsync();
            Log.Information("Configuration reload: {Status}, revision {Revision}.", result.Status, result.Revision);
            foreach (ConfigurationDiagnostic diagnostic in result.Diagnostics)
            {
                // Codes/known locations suffice; do not log documents, preset values or exception messages.
                Log.Warning("Configuration diagnostic {Code} at {Location}; warning: {Warning}; context {ContextId}, binding {BindingId}, command {CommandId}, parameter {ParameterId}.",
                    diagnostic.Code, diagnostic.Location, diagnostic.Warning, SafeDiagnosticId(diagnostic.Routing?.ContextId),
                    SafeDiagnosticId(diagnostic.Routing?.BindingId ?? diagnostic.Command?.BindingId), SafeDiagnosticId(diagnostic.Command?.CommandId), SafeDiagnosticId(diagnostic.Command?.ParameterId));
            }

            if (!this.Dispatcher.HasShutdownStarted)
            {
                TrayIconHandler.SetConfigurationStatus(result.Summary);
            }
        }
        catch (Exception error)
        {
            Log.Warning("Configuration reload raised {ExceptionType}.", error.GetType().Name);
            if (!this.Dispatcher.HasShutdownStarted)
            {
                TrayIconHandler.SetConfigurationStatus("Reload failed; see log");
            }
        }
    }

    private static string? SafeDiagnosticId(string? id) => StableIdentifier.IsValid(id) ? id : null;

    private void OnHotkeyActivated(object? sender, EventArgs args) => this.paletteController?.Toggle();

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs args)
    {
        base.OnExit(args);

        this.hotkeyService?.Dispose();
        this.paletteController?.Dispose();
        TrayIconHandler.Shutdown();

        Log.CloseAndFlush();
    }
}