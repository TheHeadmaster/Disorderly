using System.IO;
using System.Windows;
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
    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);

        Initialize();
        this.paletteController = new CommandPaletteController(new CommandPaletteWindow());
        TrayIconHandler.Initialize(() => this.Dispatcher.BeginInvoke(new Action(() => this.paletteController.Open())));
        this.hotkeyService = new GlobalHotkeyService(new WindowsHotkeyRegistration());
        this.hotkeyService.Activated += this.OnHotkeyActivated;
        this.hotkeyService.Start();
    }

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