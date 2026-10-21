using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ABExplode.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PlatformText.Text = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture} · .NET {Environment.Version}";
    }
}
