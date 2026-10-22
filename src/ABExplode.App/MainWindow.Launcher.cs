using ABExplode.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
#if WINDOWS
using ABExplode.Platform.Windows;
#else
using ABExplode.Platform.Linux;
#endif

namespace ABExplode.App;

/// <summary>
/// RESET JOGOS · Biblioteca: lista os jogos instalados, guarda um perfil por jogo e inicia aplicando o perfil.
/// A lista vem da plataforma (IGameLibrary); iniciar o jogo, achar o .exe e as opções da Steam ainda são só do Windows.
/// </summary>
public partial class MainWindow
{
    private List<LibGame> _libGames = new();
    private LibGame? _lg;
    private GameProfile? _lp;
    private bool _lLoading, _launcherInited;
    private readonly Dictionary<string, string?> _exeGuess = new();

    private static readonly (string Label, string Token, string? Hint)[] ExtraPresets =
    {
        ("Pular intro (-novid)", "-novid", null), ("Prioridade alta (-high)", "-high", null), ("Sem limite de FPS (+fps_max 0)", "+fps_max 0", null),
        ("Sem joystick (-nojoy)", "-nojoy", null), ("Abrir console (-console)", "-console", null),
        ("Programas de terceiros (-allow_third_party_software)", "-allow_third_party_software",
         "CS2: libera programas que o modo confiável bloqueia, como a captura de jogo do OBS, ReShade e alguns overlays.\n" +
         "Atenção: a Valve diz que usar esta opção pode reduzir o seu Fator de Confiança (Trust Factor) no matchmaking."),
    };

    private void InitLauncher()
    {
        if (_launcherInited) return;
        _launcherInited = true;
#if !WINDOWS
        LinuxLauncherTexts();
#endif
        foreach (var (label, token, hint) in ExtraPresets)
        {
            var b = new Button
            {
                Content = label, Tag = token, Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("OutlineButton"),
                Padding = new Thickness(10, 4, 10, 4), FontSize = 11, Margin = new Thickness(0, 0, 6, 6),
            };
            if (hint != null) ToolTip.SetTip(b, hint);
            b.Click += (_, _) =>
            {
                string t = (string)b.Tag!;
                string cur = LExtra.Text ?? "";
                if (!cur.Contains(t, StringComparison.OrdinalIgnoreCase))
                    LExtra.Text = (cur.Trim() + " " + t).Trim();
            };
            LExtraPresets.Children.Add(b);
        }
        _ = ScanGamesAsync();
    }

    // ---------------- Lista ----------------

    private async Task ScanGamesAsync()
    {
        BtnScanGames.IsEnabled = false;
        LGameCount.Text = "Procurando seus jogos…";
        try
        {
            var custom = _settings.CustomGames.ToList();
            _libGames = await Task.Run(() => _platform.Games.Scan(custom));
            ApplyGameFilter();
            var byStore = _libGames.GroupBy(g => g.Store).Select(x => $"{x.Key} {x.Count()}");
            LGameCount.Text = _libGames.Count == 0
                ? $"Nenhum jogo encontrado. Use {BtnAddExe.Content} para incluir um à mão."
                : $"{_libGames.Count} jogos · {string.Join(" · ", byStore)}";
        }
        catch (Exception ex) { LGameCount.Text = "Falhou: " + ex.Message; }
        finally { BtnScanGames.IsEnabled = true; }
    }

    private void ApplyGameFilter()
    {
        string q = LGameSearch.Text?.Trim() ?? "";
        string? keep = _lg?.Key;
        var list = _libGames.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        LGameList.ItemsSource = list;
        if (keep != null) LGameList.SelectedItem = list.FirstOrDefault(g => g.Key == keep);
    }

    private void LGameSearch_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_launcherInited) ApplyGameFilter();
    }

    private async void BtnScanGames_Click(object? sender, RoutedEventArgs e) => await ScanGamesAsync();

    /// <summary>Escolhe um arquivo pela janela do sistema; null se cancelar.</summary>
    // No Linux o jogo pode ser um binário sem extensão, um .sh, um .AppImage ou um .exe (Wine): qualquer arquivo.
    private static string GameFileTitle => OperatingSystem.IsWindows() ? "Escolha o .exe do jogo" : "Escolha o executável do jogo";
    private static (string Name, string[] Patterns) GameFileFilter =>
        OperatingSystem.IsWindows() ? ("Programas (*.exe)", new[] { "*.exe" }) : ("Todos os arquivos", new[] { "*" });
    private static string[] HelperPatterns => OperatingSystem.IsWindows() ? new[] { "*.exe", "*.bat", "*.cmd", "*.lnk" } : new[] { "*" };

    private async Task<string?> PickFileAsync(string title, string filterName, string[] patterns, string? startDir = null)
    {
        var start = startDir != null && Directory.Exists(startDir) ? await StorageProvider.TryGetFolderFromPathAsync(startDir) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false, SuggestedStartLocation = start,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void BtnAddExe_Click(object? sender, RoutedEventArgs e)
    {
        string? exe = await PickFileAsync(GameFileTitle, GameFileFilter.Name, GameFileFilter.Patterns);
        if (exe == null) return;
        if (!_settings.CustomGames.Any(c => c.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.CustomGames.Add(new CustomGame { Name = Path.GetFileNameWithoutExtension(exe), Exe = exe });
            _settings.Save();
        }
        await ScanGamesAsync();
        LGameList.SelectedItem = (LGameList.ItemsSource as IEnumerable<LibGame>)?.FirstOrDefault(g => g.Store == "Manual" && g.Id == exe);
    }

    // ---------------- Perfil ----------------

    private static void SetChip(Panel panel, string tag)
    {
        var chips = panel.Children.OfType<RadioButton>().ToList();
        var hit = chips.FirstOrDefault(c => (string?)c.Tag == tag) ?? chips.FirstOrDefault();
        if (hit != null) hit.IsChecked = true;
    }

    private async void LGameList_Changed(object? sender, SelectionChangedEventArgs e)
    {
        _lg = LGameList.SelectedItem as LibGame;
        if (_lg == null) { LDetail.IsVisible = false; LEmpty.IsVisible = true; _lp = null; return; }
        LDetail.IsVisible = true;
        LEmpty.IsVisible = false;

        var g = _lg;
        if (!_settings.GameProfiles.TryGetValue(g.Key, out var p)) { p = new GameProfile(); _settings.GameProfiles[g.Key] = p; }
        _lLoading = true;
        _lp = p;
        LTitle.Text = g.Name;
        LSub.Text = $"{g.Store} · {g.InstallDir}";
        SetChip(LPriorityPanel, p.Priority);
        LChkPlan.IsChecked = p.HighPerfPlan;
        LChkGpu.IsChecked = p.GpuHigh;
        LChkFso.IsChecked = p.DisableFso;
        SetChip(LModePanel, p.WinMode);
        SetChip(LResPanel, p.Res);
        SetChip(LHzPanel, p.Hz.ToString());
        SetChip(LStylePanel, p.Style);
        LExtra.Text = p.Extra;
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = p.Helpers;
        LSteamBox.IsVisible = g.IsSteam;
        LSteamNow.Text = g.IsSteam ? "Opções atuais na Steam: " + (SteamLaunchOptions(g.Id) is { Length: > 0 } cur ? cur : "(nenhuma)") : "";
        LGfxNote.Text = g.IsSteam
            ? "Os argumentos só passam a valer na Steam depois de APLICAR NAS OPÇÕES DA STEAM (a Steam é fechada nessa hora). Depois, o JOGAR abre pela Steam normalmente."
            : g.LaunchUri != null
                ? "Esta loja abre o jogo por um atalho que não aceita argumentos: use as opções de inicialização da própria loja. Desempenho e programas auxiliares funcionam normalmente."
                : "Os argumentos são enviados direto ao .exe quando você clica em JOGAR.";
        LStatus.Text = "";
        _lLoading = false;
        UpdateLauncherPreview();
        await ShowExeAsync(g, p);
    }

    private async Task ShowExeAsync(LibGame g, GameProfile p)
    {
        string? exe = p.ExePath ?? g.ExePath;
        if (exe == null)
        {
            LExeText.Text = "Procurando o executável…";
            if (!_exeGuess.TryGetValue(g.Key, out exe))
            {
                string dir = g.InstallDir, name = g.Name;
                exe = await Task.Run(() => FindMainExe(dir, name));
                _exeGuess[g.Key] = exe;
            }
            if (_lg != g) return; // o usuário já trocou de jogo
            LExeText.Text = exe != null ? $"Executável (detectado, confirme): {exe}" : "Executável não identificado. Ele é descoberto ao jogar, ou escolha manualmente.";
        }
        else LExeText.Text = "Executável: " + exe;
    }

    private void SaveProfileFromControls()
    {
        if (_lLoading || _lp == null) return;
        _lp.Priority = CheckedTag(LPriorityPanel) ?? "normal";
        _lp.HighPerfPlan = LChkPlan.IsChecked == true;
        _lp.GpuHigh = LChkGpu.IsChecked == true;
        _lp.DisableFso = LChkFso.IsChecked == true;
        _lp.WinMode = CheckedTag(LModePanel) ?? "default";
        _lp.Res = CheckedTag(LResPanel) ?? "default";
        _lp.Hz = int.TryParse(CheckedTag(LHzPanel), out int hz) ? hz : 0;
        _lp.Style = CheckedTag(LStylePanel) ?? "source";
        _lp.Extra = LExtra.Text?.Trim() ?? "";
        try { _settings.Save(); } catch { }
        UpdateLauncherPreview();
    }

    private void LProfile_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: false }) return; // o chip que foi desmarcado
        SaveProfileFromControls();
    }

    private void LExtra_Changed(object? sender, TextChangedEventArgs e) => SaveProfileFromControls();

    private void UpdateLauncherPreview()
    {
        if (_lp == null) return;
        string a = BuildLaunchArgs(_lp);
        LPreview.Text = a.Length > 0 ? a : "(nenhum argumento)";
    }

    private void BtnRecommended_Click(object? sender, RoutedEventArgs e)
    {
        if (_lp == null) return;
        _lLoading = true;
        SetChip(LPriorityPanel, "high");
        LChkPlan.IsChecked = true;
        LChkGpu.IsChecked = true;
        LChkFso.IsChecked = true;
        _lLoading = false;
        SaveProfileFromControls();
        LStatus.Text = "Perfil para mais FPS ligado: prioridade alta, Alto desempenho, GPU dedicada e sem otimizações de tela cheia.";
    }

    private async void BtnPickExe_Click(object? sender, RoutedEventArgs e)
    {
        if (_lg == null || _lp == null) return;
        var (g, p) = (_lg, _lp);
        string? exe = await PickFileAsync(GameFileTitle, GameFileFilter.Name, GameFileFilter.Patterns, g.InstallDir);
        if (exe == null) return;
        p.ExePath = exe;
        try { _settings.Save(); } catch { }
        await ShowExeAsync(g, p);
    }

    // ---------------- Programas auxiliares ----------------

    private async void BtnAddHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (_lp == null) return;
        var p = _lp;
        string? path = await PickFileAsync("Programa para abrir junto com o jogo", "Programas e scripts", HelperPatterns);
        if (path == null) return;
        p.Helpers.Add(new HelperProgram { Path = path, CloseWithGame = LHelperClose.IsChecked == true });
        try { _settings.Save(); } catch { }
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = p.Helpers;
    }

    private void BtnRemoveHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (_lp == null || (sender as Control)?.Tag is not HelperProgram h) return;
        _lp.Helpers.Remove(h);
        try { _settings.Save(); } catch { }
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = _lp.Helpers;
    }

#if WINDOWS
    // ---------------- Windows: .exe, argumentos, Steam e iniciar o jogo ----------------

    private CancellationTokenSource? _launchCts;

    /// <summary>Para de acompanhar o jogo iniciado (ao fechar o app).</summary>
    private void CancelLaunch() => _launchCts?.Cancel();

    private static string? FindMainExe(string dir, string name) => GameLauncher.FindMainExe(dir, name);
    private static string BuildLaunchArgs(GameProfile p) => GameLauncher.BuildArgs(p);
    private static string? SteamLaunchOptions(string appId) => SteamInfo.GetLaunchOptions(appId);

    private async void BtnSteamApply_Click(object? sender, RoutedEventArgs e)
    {
        var g = _lg;
        if (g == null || !g.IsSteam || _lp == null) return;
        string opts = GameLauncher.BuildArgs(_lp);
        BtnSteamApply.IsEnabled = false;
        try
        {
            if (SteamInfo.IsRunning)
            {
                bool yes = await Dialog.Ask(this, "RESET JOGOS · Steam",
                    "A Steam precisa ser fechada para gravar as opções de inicialização (senão ela as sobrescreve ao sair).\n\n" +
                    "Fechar a Steam agora? Jogos abertos serão encerrados. Feito um backup do arquivo antes de alterar.");
                if (!yes) return;
                LStatus.Text = "Fechando a Steam…";
                if (!await SteamInfo.ShutdownAsync()) { LStatus.Text = "A Steam não fechou. Feche-a manualmente e tente de novo."; return; }
            }
            string? err = await Task.Run(() => SteamInfo.SetLaunchOptions(g.Id, opts));
            LStatus.Text = err ?? (opts.Length > 0 ? $"Opções gravadas na Steam: {opts}. Clique em JOGAR (a Steam abre sozinha)." : "Opções da Steam limpas para este jogo.");
            LSteamNow.Text = "Opções atuais na Steam: " + (SteamInfo.GetLaunchOptions(g.Id) is { Length: > 0 } cur ? cur : "(nenhuma)");
        }
        finally { BtnSteamApply.IsEnabled = true; }
    }

    private async void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (_launchCts != null) { _launchCts.Cancel(); return; }
        var g = _lg;
        var p = _lp;
        if (g == null || p == null) return;

        _launchCts = new CancellationTokenSource();
        BtnPlay.Content = "PARAR ACOMPANHAMENTO";
        try
        {
            await GameLauncher.RunAsync(g, p,
                t => Dispatcher.UIThread.Post(() => LStatus.Text = t),
                exe => Dispatcher.UIThread.Post(() =>
                {
                    if (!string.Equals(p.ExePath, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.ExePath = exe;
                        try { _settings.Save(); } catch { }
                        if (_lg == g) LExeText.Text = "Executável: " + exe;
                    }
                }),
                _launchCts.Token);
        }
        finally
        {
            _launchCts?.Dispose();
            _launchCts = null;
            BtnPlay.Content = "JOGAR";
        }
    }
#else
    // ---------------- Linux: executável, argumentos, Steam (localconfig.vdf) e iniciar o jogo ----------------

    private CancellationTokenSource? _launchCts;
    private bool _launcherTexts;

    /// <summary>Para de acompanhar o jogo iniciado (ao fechar o app).</summary>
    private void CancelLaunch() => _launchCts?.Cancel();

    /// <summary>Textos do perfil no Linux (o que cada opção vira aqui).</summary>
    private void LinuxLauncherTexts()
    {
        if (_launcherTexts) return;
        _launcherTexts = true;
        BtnAddExe.Content = "+ ADICIONAR JOGO";
        BtnPickExe.Content = "ESCOLHER O EXECUTÁVEL";
        LChkPlan.Content = "Perfil de energia Desempenho enquanto joga (volta ao normal depois)";
        LChkGpu.Content = "Usar a placa de vídeo dedicada (notebooks com duas placas: PRIME offload)";
        LChkFso.IsVisible = false; // não existe no Linux
        LPerfHint.Text = "Prioridade e perfil de energia valem só durante o jogo. A prioridade usa o GameMode (gamemoderun), que precisa estar " +
                         "instalado (RESET OTIMIZA). Na Steam, prioridade e placa dedicada viram prefixos antes do %command% nas opções de " +
                         "inicialização; jogos de Windows rodam pelo Proton da própria Steam.";
    }

    private static string? FindMainExe(string dir, string name) => LinuxGameLauncher.FindMainExe(dir, name);

    // Na Steam do Linux os prefixos (gamemoderun, PRIME) entram antes do %command%; nos outros, só os argumentos.
    private string BuildLaunchArgs(GameProfile p)
    {
        LinuxLauncherTexts();
        return _lg?.IsSteam == true ? LinuxGameLauncher.BuildSteamOptions(p, _settings.MangoHudInGames) : GameArgs.Build(p);
    }

    private static string? SteamLaunchOptions(string appId) => LinuxGameLauncher.GetLaunchOptions(appId);

    private async void BtnSteamApply_Click(object? sender, RoutedEventArgs e)
    {
        var g = _lg;
        if (g == null || !g.IsSteam || _lp == null) return;
        string opts = LinuxGameLauncher.BuildSteamOptions(_lp, _settings.MangoHudInGames);
        BtnSteamApply.IsEnabled = false;
        try
        {
            if (LinuxGameLauncher.SteamRunning)
            {
                bool yes = await Dialog.Ask(this, "RESET JOGOS · Steam",
                    "A Steam precisa ser fechada para gravar as opções de inicialização (senão ela as sobrescreve ao sair).\n\n" +
                    "Fechar a Steam agora? Jogos abertos serão encerrados. É feito um backup do arquivo antes de alterar.");
                if (!yes) return;
                LStatus.Text = "Fechando a Steam…";
                if (!await LinuxGameLauncher.ShutdownSteamAsync()) { LStatus.Text = "A Steam não fechou. Feche-a manualmente e tente de novo."; return; }
            }
            string? err = await Task.Run(() => LinuxGameLauncher.SetLaunchOptions(g.Id, opts));
            LStatus.Text = err ?? (opts.Length > 0 ? $"Opções gravadas na Steam: {opts}. Clique em JOGAR (a Steam abre sozinha)." : "Opções da Steam limpas para este jogo.");
            LSteamNow.Text = "Opções atuais na Steam: " + (LinuxGameLauncher.GetLaunchOptions(g.Id) is { Length: > 0 } cur ? cur : "(nenhuma)");
        }
        finally { BtnSteamApply.IsEnabled = true; }
    }

    private async void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (_launchCts != null) { _launchCts.Cancel(); return; }
        var g = _lg;
        var p = _lp;
        if (g == null || p == null) return;

        _launchCts = new CancellationTokenSource();
        BtnPlay.Content = "PARAR ACOMPANHAMENTO";
        try
        {
            await LinuxGameLauncher.RunAsync(g, p, _settings.MangoHudInGames,
                t => Dispatcher.UIThread.Post(() => LStatus.Text = t),
                exe => Dispatcher.UIThread.Post(() =>
                {
                    if (!string.Equals(p.ExePath, exe, StringComparison.Ordinal))
                    {
                        p.ExePath = exe;
                        try { _settings.Save(); } catch { }
                        if (_lg == g) LExeText.Text = "Executável: " + exe;
                    }
                }),
                _launchCts.Token);
        }
        finally
        {
            _launchCts?.Dispose();
            _launchCts = null;
            BtnPlay.Content = "JOGAR";
        }
    }
#endif
}
