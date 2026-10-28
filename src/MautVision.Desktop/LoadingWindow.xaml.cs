using System.Windows;
using MautVision.Desktop.Host;

namespace MautVision.Desktop;

public partial class LoadingWindow : Window
{
    public LoadingWindow()
    {
        InitializeComponent();
        Message.Text = Strings.LoadingWorkspace;
    }
}
