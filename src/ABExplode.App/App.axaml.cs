using ABExplode.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ABExplode.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = CreatePlatform();
            AppSettings.Secrets = platform.Secrets; // antes de qualquer leitura da chave da API
            desktop.MainWindow = new MainWindow(platform);
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static IPlatform CreatePlatform()
    {
#if WINDOWS
        return new ABExplode.Platform.Windows.WindowsPlatform();
#else
        return new ABExplode.Platform.Linux.LinuxPlatform();
#endif
    }
}
