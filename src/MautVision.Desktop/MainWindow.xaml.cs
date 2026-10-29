using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Mcp;
using MautVision.Desktop.Controllers.Mcp.ResponseDto;
using MautVision.Desktop.Controllers.Projects;
using MautVision.Desktop.Controllers.System;
using MautVision.Desktop.Host;
using MautVision.Desktop.Events;
using MautVision.Desktop.Logging;
using MautVision.Desktop.Mcp;
using MautVision.Desktop.Mcp.Tools;
using MautVision.Desktop.Resources;
using MautVision.Desktop.Transport;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop;

public partial class MainWindow : Window
{
    private readonly LaunchOptions options;
    private readonly ICore core;
    private readonly BridgeTransport transport;
    private readonly ProjectEvents projectEvents;
    private readonly BaseModelEvents baseModelEvents;
    private readonly JobEvents jobEvents;
    private readonly CatalogEvents catalogEvents;
    private readonly ImagesEvents imagesEvents;
    private readonly BackgroundsEvents backgroundsEvents;
    private readonly GenerationEvents generationEvents;
    private readonly DatasetsEvents datasetsEvents;
    private readonly TrainingEvents trainingEvents;
    private readonly ChecksEvents checksEvents;
    private readonly FileSelections fileSelections = new();
    private readonly ResourceHandles resourceHandles = new();
    private readonly ResourceServer resourceServer;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> pending = [];
    private bool closing;
    // The page allowed the close (or the window closes for a reason that must not ask it).
    private bool closeApproved;
    private bool askingClose;
    private readonly CloseGuard closeGuard;
    private readonly ProjectsController projectsController;
    private readonly IAppTray tray;
    private readonly HostSettings hostSettings;
    private readonly HostLanguage language;
    private readonly McpService mcp;
    // "Exit" (or the end of a smoke run): closing the window ends the application; otherwise it hides the window in the tray.
    private bool exiting;
    private bool leftoversStarted;
    private bool drained;
    private bool smokeStarted;
    // Windows ends the session or another program (Restart Manager of the installer) closes the application: it exits at once.
    private bool sessionEnding;
    private int exitCode;
    private CoreWebView2? webView;
    private CoreWebView2Environment? webEnvironment;
    // WebView2 processes of the window for the memory of the application; replaced on the UI thread, read by hardware.sample.
    private volatile int[] webViewProcessIds = [];

    internal MainWindow(LaunchOptions options, ICore core, ILoggerFactory loggers, HostSettings hostSettings, HostLanguage language)
    {
        InitializeComponent();
        StartupMessage.Text = Strings.PreparingApplication;
        // 1200×800 does not fit every screen (150 % on 1080p leaves about 1280×688 DIP): the first size is reduced to the work area.
        var (size, minimum) = WindowBounds.Fit(SystemParameters.WorkArea.Size);
        (Width, Height, MinWidth, MinHeight) = (size.Width, size.Height, minimum.Width, minimum.Height);
        this.options = options;
        this.core = core;
        this.hostSettings = hostSettings;
        this.language = language;
        logger = loggers.CreateLogger<MainWindow>();
        transport = new BridgeTransport(loggers.CreateLogger<BridgeTransport>());
        closeGuard = new CloseGuard(transport.PublishEvent, OnCloseRequestedByPage);
        var dialogs = new NativeDialogs(PickImageFilesAsync, PickFolderAsync, PickVideoAsync, PickSaveFileAsync, PickImageFileAsync, PickArchiveAsync);
        var controllers = ControllerRegistration.Register(transport, core, loggers, resourceHandles, OnUiReady, closeGuard, fileSelections, dialogs,
            () => webViewProcessIds, language);
        projectsController = controllers.Projects;
        // The MCP server uses the same Core and controllers as the page; it starts only after the library is shown and only when enabled.
        mcp = new McpService(hostSettings, options.WorkDirectory, () => new McpToolDependencies(core, controllers.Projects, controllers.Jobs, controllers.Overview,
            controllers.Hardware, language, mcp!.Paths, mcp.Uploads, options.WorkDirectory, () => mcp.Settings, OpenProjectForMcp), RequestMcpAttention, loggers);
        ControllerRegistration.RegisterMcp(transport, new McpController(mcp, dialogs));
        mcp.Changed += OnMcpChanged;
        mcp.CallLog.Added += OnMcpCall;
        mcp.Confirmations.Requested += OnMcpConfirmationRequested;
        mcp.Confirmations.Closed += OnMcpConfirmationClosed;
        resourceServer = new ResourceServer(core, resourceHandles, loggers.CreateLogger<ResourceServer>());
        projectEvents = new ProjectEvents(core.Projects, transport.PublishEvent);
        baseModelEvents = new BaseModelEvents(core.BaseModels, transport.PublishEvent);
        jobEvents = new JobEvents(core.Jobs, transport.PublishEvent);
        catalogEvents = new CatalogEvents(core.Projects, transport.PublishEvent);
        imagesEvents = new ImagesEvents(core.Projects, transport.PublishEvent);
        backgroundsEvents = new BackgroundsEvents(core.Projects, transport.PublishEvent);
        generationEvents = new GenerationEvents(core.Projects, transport.PublishEvent);
        datasetsEvents = new DatasetsEvents(core.Projects, transport.PublishEvent);
        trainingEvents = new TrainingEvents(core.Projects, core.TrainingPresets, transport.PublishEvent);
        checksEvents = new ChecksEvents(core.Projects, resourceHandles, transport.PublishEvent, loggers.CreateLogger<ChecksEvents>());
        transport.EventReady += OnEvent;
        // Smoke runs never put an icon in the notification area; they click the items of a recording tray.
        tray = options.SmokeReport is null ? new NotifyTray() : new SmokeTray();
        tray.OpenRequested += ShowFromTray;
        tray.QuitRequested += RequestQuit;
        language.Changed += OnLanguageChanged;
        if (options.SmokeReport is not null)
        {
            ShowInTaskbar = false;
            ShowActivated = false;
        }
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (options.SmokeReport is not null && SmokeRunner.ForcedDeviceScale() is { } forced)
        {
            // A smoke run at a forced page scale gets the CSS viewport of the window a 1080p monitor has at that scale (it may exceed this screen).
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            var window = WindowBounds.Fit(WindowBounds.FullHdWorkArea(forced)).Window;
            (Width, Height) = (window.Width * forced / dpi, window.Height * forced / dpi);
        }
        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            if (!File.Exists(Path.Combine(webRoot, "index.html")))
            {
                throw new InvalidOperationException(Strings.WebBuildMissing);
            }
            var profile = Path.Combine(options.WorkDirectory, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            logger.LogInformation("WebView2 runtime {Version} is used.", environment.BrowserVersionString);
            if (closing) return;
            webEnvironment = environment;
            environment.ProcessInfosChanged += OnProcessInfosChanged;
            await Browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            StartupTimings.Log(logger, StartupTimings.WebViewReady);
            var web = Browser.CoreWebView2;
            webView = web;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = true;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            // SmartScreen cannot rate the local UI host and waits for its 2 s timeout before every navigation;
            // navigation is limited to that host by OnNavigationStarting, so the check adds nothing.
            web.Settings.IsReputationCheckingRequired = false;
            web.SetVirtualHostNameToFolderMapping(WebViewPolicy.UiHost, webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            resourceServer.Attach(web);
            web.NavigationStarting += OnNavigationStarting;
            web.FrameNavigationStarting += OnFrameNavigationStarting;
            web.NewWindowRequested += OnNewWindowRequested;
            web.PermissionRequested += OnPermissionRequested;
            web.WebMessageReceived += OnMessage;
            web.NavigationCompleted += OnNavigationCompleted;
            web.ProcessFailed += OnProcessFailed;
            UpdateWebViewProcesses();
            web.Navigate("https://" + WebViewPolicy.UiHost + "/index.html");
        }
        catch (Exception error)
        {
            logger.LogError(error, "WebView2 initialization failed.");
            if (closing) return;
            StartupMessage.Text = error is WebView2RuntimeNotFoundException
                ? Strings.WebViewRuntimeMissing
                : Strings.OpenFailed(error.Message);
            StartupMessage.Visibility = Visibility.Visible;
            if (options.SmokeReport is not null)
            {
                SmokeRunner.WriteFailure(options.SmokeReport, error);
                exitCode = 1;
                ExitWithoutAsking();
            }
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (closing) return;
        if (!args.IsSuccess)
        {
            logger.LogWarning("UI navigation failed: {Status}.", args.WebErrorStatus);
            StartupMessage.Text = Strings.InterfaceLoadFailed(args.WebErrorStatus);
            return;
        }
        StartupMessage.Visibility = Visibility.Collapsed;
        StartupTimings.Log(logger, StartupTimings.NavigationCompleted);
        if (options.SmokeReport is not null && !smokeStarted)
        {
            smokeStarted = true;
            var work = SmokeRunner.RunAsync(Browser.CoreWebView2, options.SmokeReport, options.WorkDirectory, core, resourceHandles, fileSelections, mcp,
                new SmokeCloseProbe(closeGuard, () => SmokeCloseRequests), minimized => WindowState = minimized ? WindowState.Minimized : WindowState.Normal,
                new SmokeHostProbe((SmokeTray)tray, Close, () => IsVisible, (count, answer) => (SmokeActiveWork, SmokeQuitAnswer) = (count, answer),
                    () => SmokeQuitQuestions.ToArray(), () => Activations),
                new SmokeLayoutProbe(SetSmokeBrowserSize, () => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX,
                    () => new Size(ActualWidth - Browser.ActualWidth, ActualHeight - Browser.ActualHeight)),
                lifetime.Token);
            pending.Add(work);
            try
            {
                var passed = await work;
                exitCode = passed ? 0 : 1;
                ExitWithoutAsking();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { pending.Remove(work); }
        }
    }

    /// <summary>Smoke seam: an explicit size of the WebView2 control in DIP (it may exceed the window), null — the window's size.</summary>
    private void SetSmokeBrowserSize(Size? size)
    {
        Browser.Width = size?.Width ?? double.NaN;
        Browser.Height = size?.Height ?? double.NaN;
        Browser.HorizontalAlignment = size is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Browser.VerticalAlignment = size is null ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (closing || !WebViewPolicy.IsTrusted(args.Source)) return;
        var work = HandleAsync(args.WebMessageAsJson);
        pending.Add(work);
        try
        {
            await work;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Bridge request handling failed.");
        }
        finally
        {
            pending.Remove(work);
        }
    }

    private async Task HandleAsync(string message)
    {
        var response = await transport.ExecuteAsync(message, lifetime.Token);
        if (closing || webView is null) return;
        try
        {
            webView.PostWebMessageAsJson(response.Json);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Bridge response {Method} {RequestId} delivery failed.", response.Method, response.RequestId);
        }
    }

    /// <summary>
    /// The UI showed the library after startup: leftovers of deleted projects and unfinished imports are removed in the background (never
    /// before the library is shown); with --exit-when-ready the run ends here (perf.ps1).
    /// </summary>
    private void OnUiReady()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!leftoversStarted)
            {
                leftoversStarted = true;
                _ = core.Projects.CleanUpLeftoversAsync(lifetime.Token).ContinueWith(task => logger.LogWarning(task.Exception, "Cleanup of leftovers failed."),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _ = mcp.StartIfEnabledAsync(lifetime.Token).ContinueWith(task => logger.LogWarning(task.Exception, "MCP server start failed."),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            if (!options.ExitWhenReady || options.SmokeReport is not null) return;
            closeApproved = true;
            ExitWithoutAsking();
        });
    }

    /// <summary>The application ends without the quit question (a smoke run ended, perf, a failure in a smoke run); unsaved changes are still asked.</summary>
    private void ExitWithoutAsking()
    {
        exiting = true;
        if (!closing) Close();
    }

    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;

    /// <summary>Time a session end gives running export and import to remove their temporary files.</summary>
    private static readonly TimeSpan SessionTransferWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The end of the Windows session and Restart Manager (an installer updating the application) send WM_QUERYENDSESSION and
    /// WM_ENDSESSION to the windows of the process, the hidden one too: the application exits instead of hiding in the tray.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmQueryEndSession) EndForSession("End of session requested (WM_QUERYENDSESSION, 0x" + lParam.ToString("X") + ")");
        else if (message == WmEndSession && wParam != IntPtr.Zero && !closing)
        {
            EndForSession("Session ends (WM_ENDSESSION)");
            Close();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Windows ends the session or another program closes the application: no tray, no quit question, no unsaved changes guard. Running
    /// export and import are cancelled with a short wait for their temporary files; the rest of the exit cannot wait, since WPF shuts the
    /// dispatcher down right after the session question.
    /// </summary>
    internal void EndForSession(string reason)
    {
        if (sessionEnding) return;
        sessionEnding = true;
        exiting = true;
        closeApproved = true;
        logger.LogInformation("{Reason}: the application exits.", reason);
        try
        {
            // The only blocking wait on the UI thread: after the session question the dispatcher does not run continuations any more.
            // Core does not need the UI thread, the task runs on the pool and the wait is bounded.
            if (!Task.Run(() => projectsController.CancelTransfersAsync(SessionTransferWait)).Wait(SessionTransferWait + TimeSpan.FromSeconds(1)))
                logger.LogWarning("Transfers did not stop before the end of the session.");
        }
        catch (Exception error)
        {
            logger.LogError(error, "Transfers were not cancelled before the end of the session.");
        }
    }

    /// <summary>Shows the window from the notification area (or a minimized one) with its page visible.</summary>
    internal void ShowFromTray()
    {
        if (closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Browser.Visibility = Visibility.Visible;
        if (options.SmokeReport is null) Activate();
    }

    /// <summary>A later launch with the same work directory asked this instance to show itself.</summary>
    internal void ActivateFromSecondInstance()
    {
        Activations++;
        ShowFromTray();
    }

    /// <summary>Smoke seam: how many times a later launch activated this instance.</summary>
    internal int Activations { get; private set; }

    /// <summary>
    /// Closing the window hides it in the notification area: the page becomes hidden (visibilityState) as in a minimized window, work goes
    /// on. The first time a notice tells where the application went; the fact is kept in the work directory.
    /// </summary>
    private void HideToTray()
    {
        Hide();
        Browser.Visibility = Visibility.Hidden;
        if (hostSettings.TrayNoticeShown) return;
        tray.ShowNotice(Strings.TrayNotice);
        hostSettings.MarkTrayNoticeShown();
    }

    /// <summary>
    /// "Exit" of the tray: shows the window, asks when background work runs (jobs of open projects, an import), then closes it through
    /// the unsaved changes guard of the page. The question is a native message box: it works even when the page does not answer.
    /// </summary>
    internal void RequestQuit()
    {
        if (closing) return;
        ShowFromTray();
        var count = SmokeActiveWork ?? core.Jobs.CountActive() + core.Projects.ListImports().Count(import => import.Status == ProjectImportStatus.Running);
        if (count > 0 && !ConfirmQuit(QuitQuestion(count))) return;
        exiting = true;
        Close();
    }

    internal static string QuitQuestion(int count) => Strings.QuitQuestion(count);

    /// <summary>
    /// The interface language changed (settings.setLanguage): the tray is relabelled on the UI thread and the page is told; the loading
    /// window uses the language from the next start.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!closing) tray.ApplyTexts();
        });
        transport.PublishEvent("settings.languageChanged", Controllers.Settings.SettingsController.Map(language));
    }

    private void OnMcpChanged() => transport.PublishEvent("mcp.stateChanged", McpController.Map(mcp));

    private void OnMcpCall(McpCallEntry entry) => transport.PublishEvent("mcp.callAdded", McpController.Map(entry));

    private void OnMcpConfirmationRequested(McpConfirmation confirmation) => transport.PublishEvent("mcp.confirmationRequested", McpController.Map(confirmation));

    private void OnMcpConfirmationClosed(Guid id) => transport.PublishEvent("mcp.confirmationClosed", new McpConfirmationClosedEventDto(id));

    /// <summary>A confirmation of an AI client waits for the user: the window comes out of the tray (or a minimized state) to the front.</summary>
    private void RequestMcpAttention()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.InvokeAsync(ShowFromTray);
    }

    /// <summary>open_project_in_app: the page opens the project (through its unsaved changes guard) and the window comes to the front.</summary>
    private void OpenProjectForMcp(string projectId)
    {
        RequestMcpAttention();
        transport.PublishEvent("mcp.openProject", new McpOpenProjectEventDto(projectId));
    }

    private bool ConfirmQuit(string question)
    {
        if (options.SmokeReport is not null)
        {
            SmokeQuitQuestions.Add(question);
            return SmokeQuitAnswer;
        }
        return MessageBox.Show(this, question, "Maut Vision", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>Smoke seams of "Exit": the work count to use instead of Core's, the answer to the question and the questions asked.</summary>
    internal int? SmokeActiveWork { get; set; }

    internal bool SmokeQuitAnswer { get; set; }

    internal List<string> SmokeQuitQuestions { get; } = [];

    /// <summary>Smoke runs count the page's close requests instead of closing.</summary>
    internal int SmokeCloseRequests { get; private set; }

    /// <summary>system.close: the page saved or dropped its unsaved changes after the user tried to close the window.</summary>
    private void OnCloseRequestedByPage()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (options.SmokeReport is not null)
            {
                SmokeCloseRequests++;
                return;
            }
            closeApproved = true;
            ExitWithoutAsking();
        });
    }

    /// <summary>
    /// Smoke-only answer of the image files dialog: the next <see cref="PickImageFilesAsync"/> of a smoke run returns these
    /// paths once; without it the smoke dialog answers as closed.
    /// </summary>
    internal static IReadOnlyList<string>? SmokeFilesAnswer;

    /// <summary>
    /// Native multi-select dialog for images (class images, image groups, backgrounds), owned by this window. Smoke runs never
    /// show it: they register fixture files in the selections directly or set <see cref="SmokeFilesAnswer"/>.
    /// </summary>
    private Task<IReadOnlyList<string>?> PickImageFilesAsync(string title, CancellationToken cancellationToken)
    {
        if (options.SmokeReport is not null) return Task.FromResult(Interlocked.Exchange(ref SmokeFilesAnswer, null));
        if (closing) return Task.FromResult<IReadOnlyList<string>?>(null);
        return Dispatcher.InvokeAsync<IReadOnlyList<string>?>(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = Strings.FilterImages + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
                Multiselect = true,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <summary>
    /// Native single-file dialog for one image (a model check) owned by this window; in smoke runs it returns the first path of
    /// <see cref="SmokeFilesAnswer"/>.
    /// </summary>
    private Task<string?> PickImageFileAsync(string title, CancellationToken cancellationToken)
    {
        if (options.SmokeReport is not null) return Task.FromResult(Interlocked.Exchange(ref SmokeFilesAnswer, null)?.FirstOrDefault());
        if (closing) return Task.FromResult<string?>(null);
        return Dispatcher.InvokeAsync<string?>(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = Strings.FilterImages + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
                Multiselect = false,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <summary>Native folder dialog owned by this window; never shown in smoke runs.</summary>
    private Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken)
    {
        if (options.SmokeReport is not null || closing) return Task.FromResult<string?>(null);
        return Dispatcher.InvokeAsync<string?>(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <summary>
    /// Smoke-only answer of the video dialog: the next <see cref="PickVideoAsync"/> of a smoke run returns this path once
    /// (the dialog itself is never shown in smoke runs).
    /// </summary>
    internal static string? SmokeVideoAnswer;

    /// <summary>Native single-file dialog for videos owned by this window; in smoke runs it returns <see cref="SmokeVideoAnswer"/>.</summary>
    private Task<string?> PickVideoAsync(string title, CancellationToken cancellationToken)
    {
        if (options.SmokeReport is not null) return Task.FromResult(Interlocked.Exchange(ref SmokeVideoAnswer, null));
        if (closing) return Task.FromResult<string?>(null);
        return Dispatcher.InvokeAsync<string?>(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = Strings.FilterVideos + "|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v",
                Multiselect = false,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <summary>
    /// Smoke-only answer of the save dialog: the next <see cref="PickSaveFileAsync"/> of a smoke run returns this path once
    /// (the dialog itself is never shown in smoke runs).
    /// </summary>
    internal static string? SmokeSaveAnswer;

    /// <summary>Native "Save as" for a ZIP archive owned by this window; it asks before overwriting an existing file.</summary>
    private Task<string?> PickSaveFileAsync(string title, string fileName, string filter, CancellationToken cancellationToken)
    {
        if (options.SmokeReport is not null) return Task.FromResult(Interlocked.Exchange(ref SmokeSaveAnswer, null));
        if (closing) return Task.FromResult<string?>(null);
        return Dispatcher.InvokeAsync<string?>(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = filter + " (*.zip)|*.zip",
                FileName = fileName,
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <summary>Smoke-only answer of the archive dialog: the next <see cref="PickArchiveAsync"/> of a smoke run returns this path once.</summary>
    internal static string? SmokeArchiveAnswer;

    /// <summary>Native single-file dialog for a project archive owned by this window; in smoke runs it returns <see cref="SmokeArchiveAnswer"/>.</summary>
    private Task<string?> PickArchiveAsync(string title, CancellationToken cancellationToken)
    {
        if (options.SmokeReport is not null) return Task.FromResult(Interlocked.Exchange(ref SmokeArchiveAnswer, null));
        if (closing) return Task.FromResult<string?>(null);
        return Dispatcher.InvokeAsync<string?>(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = Strings.FilterProjectArchive + " (*.zip)|*.zip",
                Multiselect = false,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    private void OnProcessInfosChanged(object? sender, object args) => UpdateWebViewProcesses();

    private void UpdateWebViewProcesses()
    {
        if (closing || webEnvironment is null) return;
        try
        {
            webViewProcessIds = webEnvironment.GetProcessInfos().Select(info => info.ProcessId).ToArray();
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "WebView2 processes could not be listed.");
        }
    }

    /// <summary>
    /// The WPF control keeps WebView2 visible in a minimized window. Hiding it while minimized makes the page see
    /// document.visibilityState = hidden (pages stop their polling) and lets WebView2 throttle it; restoring shows it again.
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (closing) return;
        Browser.Visibility = WindowState == WindowState.Minimized ? Visibility.Hidden : Visibility.Visible;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        logger.LogError("WebView2 process failed: {Kind}, reason {Reason}, exit code {ExitCode}, {Description}.",
            args.ProcessFailedKind, args.Reason, args.ExitCode, args.ProcessDescription);
        if (closing || webView is null) return;
        switch (args.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // The page state is lost with its process; a reload restores the UI from the host data.
                try
                {
                    webView.Reload();
                }
                catch (Exception error)
                {
                    logger.LogError(error, "UI reload after a WebView2 failure failed.");
                }
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The control cannot be used any more; only a restart brings the UI back.
                webView = null;
                StartupMessage.Text = Strings.WebViewCrashed(args.Reason);
                StartupMessage.Visibility = Visibility.Visible;
                if (options.SmokeReport is not null)
                {
                    SmokeRunner.WriteFailure(options.SmokeReport, new InvalidOperationException("WebView2 browser process exited: " + args.Reason));
                    exitCode = 1;
                    ExitWithoutAsking();
                }
                break;
        }
    }

    private void OnEvent(string message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (closing || webView is null) return;
            try { webView.PostWebMessageAsJson(message); }
            catch (Exception error) { logger.LogError(error, "Bridge event delivery failed."); }
        });
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (drained) return;
        args.Cancel = true;
        if (closing || askingClose) return;
        // The user cannot close a hidden window: another program asks the application to end (Restart Manager, taskkill without /F).
        if (!exiting && !IsVisible) EndForSession("The window was closed while hidden in the tray");
        if (!exiting)
        {
            HideToTray();
            return;
        }
        // Closing the window does not fire beforeunload: the page is asked about unsaved changes first (never in smoke runs,
        // which close themselves). No answer within a second closes anyway.
        if (!closeApproved && options.SmokeReport is null && webView is not null)
        {
            askingClose = true;
            bool allowed;
            try
            {
                allowed = await closeGuard.AskAsync(CloseGuard.AnswerTimeout);
            }
            finally
            {
                askingClose = false;
            }
            if (closing) return;
            if (!allowed)
            {
                // The page shows its dialog; "Leave without saving" or a save asks for the exit again (system.close).
                exiting = false;
                return;
            }
        }
        closing = true;
        tray.OpenRequested -= ShowFromTray;
        tray.QuitRequested -= RequestQuit;
        language.Changed -= OnLanguageChanged;
        mcp.Changed -= OnMcpChanged;
        mcp.CallLog.Added -= OnMcpCall;
        mcp.Confirmations.Requested -= OnMcpConfirmationRequested;
        mcp.Confirmations.Closed -= OnMcpConfirmationClosed;
        StateChanged -= OnStateChanged;
        if (webEnvironment is not null) webEnvironment.ProcessInfosChanged -= OnProcessInfosChanged;
        transport.EventReady -= OnEvent;
        projectEvents.Dispose();
        baseModelEvents.Dispose();
        jobEvents.Dispose();
        catalogEvents.Dispose();
        imagesEvents.Dispose();
        backgroundsEvents.Dispose();
        generationEvents.Dispose();
        datasetsEvents.Dispose();
        trainingEvents.Dispose();
        // A live check of the closing window stops with it (its worker would also end with the process).
        checksEvents.EndSession();
        checksEvents.Dispose();
        transport.Dispose();
        if (webView is not null)
        {
            webView.WebMessageReceived -= OnMessage;
            webView.NavigationCompleted -= OnNavigationCompleted;
        }
        if (sessionEnding)
        {
            // Transfers were cancelled in EndForSession; the window closes now (WPF may already be closing it for the shutdown).
            tray.Dispose();
            try
            {
                lifetime.Cancel();
                Application.Current.Shutdown(exitCode);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Exit at the end of the session failed.");
            }
            drained = true;
            args.Cancel = false;
            return;
        }
        await Task.Yield();
        try
        {
            // "the transfer will be cancelled": an export removes its temporary file, an import its staging folder, if they manage in time.
            await projectsController.CancelTransfersAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Transfers were not cancelled before exit.");
        }
        try
        {
            // AI clients are cut off before the bridge drains: no new tool calls reach a closing Core.
            await mcp.DisposeAsync();
        }
        catch (Exception error)
        {
            logger.LogError(error, "MCP server shutdown failed.");
        }
        try
        {
            await lifetime.CancelAsync();
        }
        catch (Exception error)
        {
            logger.LogError(error, "Bridge cancellation failed.");
        }
        try
        {
            await Task.WhenAll(pending.ToArray());
        }
        catch (Exception error)
        {
            logger.LogError(error, "Bridge drain failed.");
        }
        try
        {
            await resourceServer.CloseAsync();
        }
        catch (Exception error)
        {
            logger.LogError(error, "Resource channel shutdown failed.");
        }
        DetachWebView();
        webView = null;
        Browser.Dispose();
        lifetime.Dispose();
        tray.Dispose();
        drained = true;
        Close();
        Application.Current.Shutdown(exitCode);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = !WebViewPolicy.IsTrusted(args.Uri);
        if (!args.Cancel)
        {
            // A new document of the UI, including a reload, must ask for fresh resource URLs and choose files again.
            resourceServer.ResetSession();
            fileSelections.ResetSession();
            projectsController.ResetSession();
            // A live check is observed only by the page that started it.
            checksEvents.EndSession();
        }
    }

    private static void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
        => args.State = CoreWebView2PermissionState.Deny;

    private void DetachWebView()
    {
        if (webView is null) return;
        webView.WebMessageReceived -= OnMessage;
        webView.NavigationCompleted -= OnNavigationCompleted;
        webView.ProcessFailed -= OnProcessFailed;
        webView.NavigationStarting -= OnNavigationStarting;
        webView.FrameNavigationStarting -= OnFrameNavigationStarting;
        webView.NewWindowRequested -= OnNewWindowRequested;
        webView.PermissionRequested -= OnPermissionRequested;
    }
}


