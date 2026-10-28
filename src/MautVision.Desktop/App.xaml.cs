using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Desktop.Host;
using MautVision.Desktop.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MautVision.Desktop;

public partial class App : Application
{
    private ApplicationLog log = ApplicationLog.Disabled;
    private ILogger logger = NullLogger.Instance;
    private bool smoke;
    private SingleInstance? instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(e.Args);
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "Не удалось запустить Maut Vision", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // One application per work directory: a second launch shows the first one's window and ends.
        instance = SingleInstance.TryAcquire(options.WorkDirectory, () => Dispatcher.InvokeAsync(OnActivatedBySecondInstance));
        if (instance is null)
        {
            Shutdown(0);
            return;
        }
        RunningMarker.Hold();
        SessionEnding += OnSessionEnding;
        using var cancellation = new CancellationTokenSource();
        var loading = new LoadingWindow();
        var switching = false;
        loading.Closed += (_, _) =>
        {
            if (!switching) cancellation.Cancel();
        };
        MainWindow = loading;
        loading.Show();
        // The log opens later, so the time of this stage is kept and written once it exists.
        var loadingShown = StartupTimings.ElapsedMilliseconds;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            smoke = options.SmokeReport is not null;
            var catalog = options.BaseModelCatalog ?? Path.Combine(AppContext.BaseDirectory, "catalog", "yolo-catalog.json");
            log = await Task.Run(() => ApplicationLog.Start(options.WorkDirectory, catalog));
            logger = log.Factory.CreateLogger<App>();
            StartupTimings.Log(logger, StartupTimings.LoadingWindowShown, loadingShown);
            await WorkDirectorySetup.PrepareAsync(options.WorkDirectory, cancellation.Token);
            var pythonWorker = options.PythonWorker(AppContext.BaseDirectory);
            logger.LogInformation("Python worker: {Mode} {Executable}.", pythonWorker?.Mode ?? "not configured", pythonWorker?.Executable ?? "");
            var core = CoreFactory.GetInstance(new CoreOptions(options.WorkDirectory, catalog)
            {
                LoggerFactory = log.Factory,
                FfmpegDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"),
                SmokeFixtures = smoke,
                PythonWorker = pythonWorker,
            });
            await core.InitializeAsync(cancellation.Token);
            StartupTimings.Log(logger, StartupTimings.CoreInitialized);
            cancellation.Token.ThrowIfCancellationRequested();
            MainWindow = new MainWindow(options, core, log.Factory);
            MainWindow.Show();
            StartupTimings.Log(logger, StartupTimings.MainWindowShown);
            switching = true;
            loading.Close();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            logger.LogInformation("Startup cancelled: the loading window was closed.");
            Shutdown();
        }
        catch (Exception error)
        {
            logger.LogError(error, "Startup failed.");
            MessageBox.Show(error.Message, "Не удалось запустить Maut Vision", MessageBoxButton.OK, MessageBoxImage.Error);
            switching = true;
            loading.Close();
            Shutdown(1);
        }
    }

    /// <summary>The main window comes out of the notification area; during startup the loading window comes to the front.</summary>
    private void OnActivatedBySecondInstance()
    {
        logger.LogInformation("A second launch with the same work directory activated this instance.");
        if (MainWindow is MainWindow main) main.ActivateFromSecondInstance();
        else MainWindow?.Activate();
    }

    /// <summary>
    /// Windows ends the session, or Restart Manager (the installer updating the application) asks it to close: WPF shuts the application
    /// down after this event, so the main window exits now without the tray, the quit question and the unsaved changes guard.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        logger.LogInformation("Windows session ending ({Reason}).", e.ReasonSessionEnding);
        if (MainWindow is MainWindow main) main.EndForSession("Windows session ending");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        using (var process = Process.GetCurrentProcess())
        {
            logger.LogInformation("Maut Vision exited with code {ExitCode}. Peak working set of the host process: {PeakWorkingSetMegabytes} MB.",
                e.ApplicationExitCode, process.PeakWorkingSet64 / 1048576);
        }
        log.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        logger.LogError(e.Exception, "Unhandled exception on the UI thread.");
        e.Handled = true;
        // A running main window keeps working; before it exists the application cannot start.
        var running = MainWindow is MainWindow;
        if (!smoke)
        {
            var details = log.FilePath is { } path ? $"\n\nПодробности записаны в журнал:\n{path}" : "";
            MessageBox.Show(running ? "Произошла непредвиденная ошибка." + details : "Не удалось запустить Maut Vision." + details,
                "Maut Vision", MessageBoxButton.OK, running ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }
        if (!running || smoke) Shutdown(1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; terminating: {Terminating}.", e.IsTerminating);
        if (e.IsTerminating) log.Dispose();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        logger.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
