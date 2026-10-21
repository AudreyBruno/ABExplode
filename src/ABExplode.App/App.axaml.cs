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
            if (platform != null)
            {
                AppSettings.Secrets = platform.Secrets; // antes de qualquer leitura da chave da API
                desktop.MainWindow = new MainWindow(platform);
            }
            else
            {
                desktop.MainWindow = new Window
                {
                    Title = "ABExplode", Width = 520, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new TextBlock
                    {
                        Text = "A versão Linux do ABExplode ainda está sendo feita (Fase 4 do MIGRACAO-AVALONIA.md).",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24),
                    },
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static IPlatform? CreatePlatform()
    {
#if WINDOWS
        return new ABExplode.Platform.Windows.WindowsPlatform();
#else
        return null;
#endif
    }
}
