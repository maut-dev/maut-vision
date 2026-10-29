using System.Windows;
using Forms = System.Windows.Forms;

namespace MautVision.Desktop.Host;

/// <summary>
/// The icon of the application in the notification area: «Open Maut Vision» (also a left click) and «Exit», and a one-time notice.
/// Events come on the UI thread; the item texts follow the interface language (<see cref="ApplyTexts"/>).
/// </summary>
internal interface IAppTray : IDisposable
{
    event Action? OpenRequested;

    event Action? QuitRequested;

    void ShowNotice(string text);

    /// <summary>Relabels the items in the current interface language.</summary>
    void ApplyTexts();
}

/// <summary>The real icon (WinForms NotifyIcon, pumped by the WPF dispatcher of the UI thread).</summary>
internal sealed class NotifyTray : IAppTray
{
    internal const string Tooltip = "Maut Vision";

    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon image;
    private readonly Forms.ToolStripItem openItem;
    private readonly Forms.ToolStripItem quitItem;

    internal NotifyTray()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/maut-vision.ico"))
            ?? throw new InvalidOperationException("The application icon is missing.");
        using (var stream = resource.Stream) image = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        var menu = new Forms.ContextMenuStrip();
        openItem = menu.Items.Add(Strings.TrayOpen, null, (_, _) => OpenRequested?.Invoke());
        quitItem = menu.Items.Add(Strings.TrayExit, null, (_, _) => QuitRequested?.Invoke());
        icon = new Forms.NotifyIcon { Icon = image, Text = Tooltip, ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? QuitRequested;

    public void ShowNotice(string text)
    {
        icon.BalloonTipTitle = Tooltip;
        icon.BalloonTipText = text;
        icon.ShowBalloonTip(5000);
    }

    public void ApplyTexts()
    {
        openItem.Text = Strings.TrayOpen;
        quitItem.Text = Strings.TrayExit;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        image.Dispose();
    }
}

/// <summary>Smoke runs never put an icon in the notification area: this tray records notices and lets the smoke click its items.</summary>
internal sealed class SmokeTray : IAppTray
{
    private readonly List<string> notices = [];

    public event Action? OpenRequested;

    public event Action? QuitRequested;

    internal IReadOnlyList<string> Notices
    {
        get
        {
            lock (notices) return notices.ToArray();
        }
    }

    public void ShowNotice(string text)
    {
        lock (notices) notices.Add(text);
    }

    /// <summary>The item texts the smoke reads after a language change.</summary>
    internal (string Open, string Quit) Texts { get; private set; } = (Strings.TrayOpen, Strings.TrayExit);

    public void ApplyTexts() => Texts = (Strings.TrayOpen, Strings.TrayExit);

    internal void ClickOpen() => OpenRequested?.Invoke();

    internal void ClickQuit() => QuitRequested?.Invoke();

    public void Dispose()
    {
    }
}
