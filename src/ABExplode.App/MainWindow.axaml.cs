using System.Diagnostics;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ABExplode.App;

/// <summary>
/// Janela principal (Avalonia). Mesma organização do app WPF: um arquivo parcial por área, code-behind e páginas trocadas
/// por visibilidade. As telas ainda não migradas mostram o aviso da PageTodo (MIGRACAO-AVALONIA.md, Fase 3).
/// </summary>
public partial class MainWindow : Window
{
    private readonly IPlatform _platform;
    private readonly ISensorSource _monitor;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _timer;

    // Placa de vídeo principal (dedicada = primeira da lista), para o rodapé do menu
    private List<GpuInfo> _gpus = new();

    /// <summary>Telas já migradas; as outras abrem a PageTodo.</summary>
    private readonly Dictionary<string, Control> _pages;

    public MainWindow() : this(null!) { } // só para o designer do Avalonia

    public MainWindow(IPlatform platform)
    {
        _platform = platform;
        InitializeComponent();
        _pages = new Control[] { PageDash, PageSystem, PageSensors, PageGpu, PageDisks, PageEnergy, PageNet, PageBench }.ToDictionary(p => p.Name!);
        if (platform == null) { _monitor = null!; _timer = null!; return; }

        ThemeManager.Apply(_settings.LightTheme);
        UpdateThemeButton();
        SideVersion.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(2) ?? "");

        _monitor = _platform.CreateSensors();
        SystemSub.Text = "Lendo informações do sistema…";

        InitDevices();
        InitBench();
        RefreshDrives();
        NavDash.IsChecked = true;

        // A janela aparece já; as coletas pesadas (WMI, sensores, GPU) rodam em paralelo fora da thread da janela.
        Opened += async (_, _) => await LoadHardwareAsync();

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            // Durante a cena de GPU (e o teste de disco) as leituras de sensores ficam pausadas: cada consulta trava o driver
            // por um instante e aparecia como engasgo no 1% low do benchmark.
            if (_gpuBenchActive) return;
            _monitor.Refresh();
            if (_monitor.IsOpen) StatusText.Text = $"{_monitor.Rows.Count} sensores · atualizado {DateTime.Now:HH:mm:ss}";
            UpdateSideBar();
            UpdateCpuSensorWarning();
            CollectDashSample();
            if (PageSensors.IsVisible) UpdateSensorList();
            if (PageGpu.IsVisible) UpdateGpuPage();
            if (PageBench.IsVisible) UpdateBenchLive();
        };
        _timer.Start();

        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _benchCts?.Cancel(); _gameBenchCts?.Cancel();
            _toast?.Close();
            CloseDevices();
            _monitor.Dispose();
        };
    }

    /// <summary>Coletas lentas da abertura, em paralelo e fora da thread da janela; cada parte preenche a tela quando fica pronta.</summary>
    private async Task LoadHardwareAsync()
    {
        async Task LoadInfo()
        {
            try
            {
                var info = await Task.Run(_platform.SystemInfo.Collect);
                InfoCategories.ItemsSource = info;
                InfoCategories.SelectedIndex = 0;
                SystemSub.Text = $"{Environment.MachineName} · {info.Count} categorias";
            }
            catch (Exception ex) { SystemSub.Text = $"Não foi possível ler as informações do sistema: {ex.Message}"; }
        }

        async Task LoadSensors()
        {
            try { await _monitor.OpenAsync(); }
            catch (Exception ex) { StatusText.Text = $"Sensores indisponíveis: {ex.Message}"; return; }
            _monitor.Refresh();
            UpdateCpuSensorWarning();
            AddChip("Todos", null, isChecked: true);
            foreach (var c in _monitor.Rows.Select(r => r.Category).Distinct().OrderBy(x => x))
                AddChip(c, c);
            UpdateDashboard();
        }

        async Task LoadGpu()
        {
            try { _gpus = await Task.Run(_platform.Gpu.Collect); }
            catch { _gpus = new(); }
            InitGpuPage();
            _cpuName = await Task.Run(_platform.SystemInfo.CpuName);
            UpdateDashboard();
        }

        await Task.WhenAll(LoadInfo(), LoadSensors(), LoadGpu());
    }

    // ---------------- Rodapé do menu: leitura ao vivo ----------------

    private void UpdateSideBar()
    {
        SideTemp.Text = _monitor.CpuTemperature is float t ? $"{t:0.#} °C" : "—";
        SideLoad.Text = _monitor.CpuLoad is float l ? $"{l:0} %" : "—";
        if (_gpus.Count == 0) return;
        SideGpuTemp.Text = MainGpuTemp() is float gt ? $"{gt:0.#} °C" : "—";
        SideGpuLoad.Text = MainGpuLoad() is float gl ? $"{gl:0} %" : "—";
    }

    // ---------------- Sensores da CPU (PawnIO) ----------------

    /// <summary>Mostra o aviso no menu quando temperatura/consumo da CPU não respondem, com o motivo provável.</summary>
    private void UpdateCpuSensorWarning()
    {
        if (!_monitor.IsOpen) return; // ainda detectando: não acusar falta de sensor antes da hora
        var p = _monitor.DiagnoseCpuSensors();
        CpuSensorWarn.IsVisible = p != CpuSensorProblem.None;
        CpuSensorWarnText.Text = p switch
        {
            CpuSensorProblem.NotElevated => "Sem permissão de administrador.",
            CpuSensorProblem.PawnIOMissing => "Falta o driver PawnIO.",
            CpuSensorProblem.ReadFailed => "O driver não respondeu.",
            _ => ""
        };
    }

    private async void BtnFixCpuSensors_Click(object? sender, RoutedEventArgs e)
    {
        var p = _monitor.DiagnoseCpuSensors();
        string why = _monitor.Explain(p);
        if (p != CpuSensorProblem.PawnIOMissing)
        {
            await Dialog.Show(this, "ABExplode", why);
            return;
        }

        // winget vem com o Windows 11 e com o Windows 10 atualizado (App Installer).
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     @"Microsoft\WindowsApps\winget.exe");
        if (!File.Exists(winget))
        {
            await Dialog.Show(this, "ABExplode", why + "\n\nO instalador de pacotes do Windows (winget) não foi encontrado neste PC. " +
                              "Atualize o \"Instalador de Aplicativo\" pela Microsoft Store e use o botão de novo, " +
                              "ou instale o PawnIO (autor: namazso) manualmente.");
            return;
        }

        if (!await Dialog.Ask(this, "ABExplode", why + "\n\nInstalar o PawnIO agora? Vai abrir uma janela com o instalador do Windows (winget). " +
                              "Quando terminar, feche o ABExplode e abra de novo.")) return;
        Process.Start(new ProcessStartInfo("cmd.exe",
            "/k winget install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements" +
            " && echo. && echo Pronto! Feche esta janela e reabra o ABExplode.")
        { UseShellExecute = true });
    }

    // ---------------- Janela / navegação ----------------

    private void Min_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } item || _pages == null) return;
        string page = (string)item.Tag!;
        string name = item.Content is Panel panel ? panel.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? "" : "";
        ShowPage(page, name);

        // Abre a categoria da página e mostra "CATEGORIA · página" na barra de título.
        if (item.Parent is Control { Name: { } items } && items.EndsWith("Items") && this.FindControl<ToggleButton>(items[..^5]) is { } group)
        {
            group.IsChecked = true;
            name = $"{group.Content} · {name}";
        }
        TitlePage.Text = name;

        // Algumas páginas carregam dados só quando abertas.
        switch (page)
        {
            case "PageDash": UpdateDashboard(); break;
            case "PageSensors": ApplySensorFilter(); break;
            case "PageGpu": UpdateGpuPage(); break;
            case "PageDisks": RefreshDiskHealth(); break;
            case "PageEnergy": RefreshEnergy(); break;
        }
    }

    private void ShowPage(string name, string title)
    {
        bool migrated = _pages.ContainsKey(name);
        foreach (var p in _pages.Values) p.IsVisible = p.Name == name;
        PageTodo.IsVisible = !migrated;
        if (!migrated) TodoTitle.Text = title;
    }

    // ---------------- Tema (escuro / claro) ----------------

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsLight);
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
        UpdateThemeButton();
    }

    /// <summary>Mostra o ícone do modo para onde o clique leva (sol = ir para o claro, lua = ir para o escuro) e troca o logo.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        BtnTheme.Content = light ? "" : "";
        ToolTip.SetTip(BtnTheme, light ? "Modo escuro" : "Modo claro");
        using var s = AssetLoader.Open(new Uri(light ? "avares://ABExplode.App/Assets/logo-dark.png" : "avares://ABExplode.App/Assets/logo.png"));
        TitleLogo.Source = new Bitmap(s);
    }
}
