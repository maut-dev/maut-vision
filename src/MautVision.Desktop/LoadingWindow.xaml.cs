using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MautVision.Desktop.Host;

namespace MautVision.Desktop;

public partial class LoadingWindow : Window
{
    public LoadingWindow()
    {
        InitializeComponent();
        Message.Text = Strings.LoadingWorkspace;
        SourceInitialized += (_, _) => WindowFrame.RoundCorners(new WindowInteropHelper(this).Handle);
    }

    /// <summary>The window has no caption: it is dragged by any point.</summary>
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
