using System.Diagnostics;
using ABExplode.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
#if WINDOWS
using ABExplode.Platform.Windows;
#else
using ABExplode.Platform.Linux;
#endif

namespace ABExplode.App;

/// <summary>
/// RESET FERRAMENTAS: Otimização, Inicialização, Instalar apps e Drivers. No Windows usam Optimizer, WindowsTweaks,
/// StartupManager, AppInstaller e DriverManager; no Linux, LinuxOptimizer, LinuxTweaks e LinuxAutostart (Instalar apps e
/// Drivers ainda avisam que não estão disponíveis: Fase 5 do MIGRACAO-AVALONIA.md). A barra de RAM vem da plataforma.
/// </summary>
public partial class MainWindow
{
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void UpdateRamStatus()
    {
        var (total, avail) = _platform.SystemInfo.MemoryStatus();
        if (total == 0) return;
        double used = total - avail;
        OptRamText.Text = $"{used / 1073741824.0:0.0} de {total / 1073741824.0:0.0} GB · {used / total:P0}";
        OptRamBar.Value = used / total;
    }

#if WINDOWS
    // ================= RESET OTIMIZA =================

    private List<BackgroundApp> _apps = new();
    private bool _optInit;

    /// <summary>Carrega a página quando ela é aberta (os ajustes do Windows só na primeira vez).</summary>
    private void InitOptimize()
    {
        if (!_optInit)
        {
            _optInit = true;
            foreach (var t in _tweaks)
                try { t.Refresh(); } catch (Exception ex) { t.Status = $"Não foi possível ler: {ex.Message}"; }
            OptTweaks.ItemsSource = _tweaks;
        }
        RefreshApps();
        UpdateRamStatus();
    }

    private void RefreshApps()
    {
        _apps = Optimizer.ListApps();
        OptApps.ItemsSource = _apps;
    }

    private void BtnRefreshApps_Click(object? sender, RoutedEventArgs e) => RefreshApps();

    private async void BtnOptimize_Click(object? sender, RoutedEventArgs e)
    {
        var toClose = _apps.Where(a => a.Selected).ToList();
        string list = toClose.Count == 0 ? "nenhum programa marcado" : string.Join("\n", toClose.Select(a => $"  • {a.Title}"));
        if (!await Dialog.Ask(this, "RESET OTIMIZA", "O RESET OTIMIZA vai:\n\n" +
                                                     "1. Limpar a memória em espera e enxugar a RAM dos programas.\n" +
                                                     $"2. Fechar os programas marcados:\n{list}\n\n" +
                                                     "Salve o que estiver aberto nesses programas antes. Continuar?")) return;

        BtnOptimize.IsEnabled = false;
        BtnOptimize.Content = "OTIMIZANDO…";
        OptResult.Text = "";
        var closeResult = await Task.Run(() => Optimizer.CloseApps(toClose));
        var mem = await Task.Run(Optimizer.CleanMemory); // depois de fechar, para contar a memória liberada por eles também
        BtnOptimize.IsEnabled = true;
        BtnOptimize.Content = "RESET OTIMIZA";

        var parts = new List<string> { $"Memória liberada: {mem.FreedBytes / 1048576.0:N0} MB" };
        parts.Add(mem.StandbyPurged ? "memória em espera limpa" : $"memória em espera não limpa ({mem.StandbyError})");
        if (closeResult.Closed.Count > 0) parts.Add($"fechados: {string.Join(", ", closeResult.Closed)}");
        if (closeResult.NotClosed.Count > 0)
            parts.Add($"não fecharam (podem ter algo não salvo ou pedir confirmação): {string.Join(", ", closeResult.NotClosed)}");
        OptResult.Text = string.Join(" · ", parts) + ".";
        RefreshApps();
        UpdateRamStatus();
    }

    private void Tweak_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Tweak t } box) return;
        bool turnOn = box.IsChecked == true;
        try
        {
            t.Write(turnOn, _settings);
            _settings.Save();
            t.Refresh();
            t.Status = t.IsOn == turnOn
                ? (turnOn ? "Aplicado." : "Desligado.") + (t.NeedsRestart ? " Reinicie o PC para valer." : "")
                : "O Windows não aceitou a mudança (pode exigir administrador).";
        }
        catch (Exception ex)
        {
            t.Refresh();
            t.Status = $"Falhou: {ex.Message}";
        }
        box.IsChecked = t.IsOn; // o binding é só leitura: volta ao estado real
    }

    private async void BtnRestoreTweaks_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings.TweakBackup.Count == 0)
        {
            await Dialog.Show(this, "RESET OTIMIZA", "Nenhum ajuste foi alterado pelo ABExplode ainda.");
            return;
        }
        if (!await Dialog.Ask(this, "RESET OTIMIZA", "Voltar todos os ajustes do Windows para como estavam antes do ABExplode alterá-los?")) return;
        bool restart = _tweaks.Any(t => t.NeedsRestart && _settings.TweakBackup.ContainsKey(t.Id));
        WindowsTweaks.RestoreAll(_tweaks, _settings);
        foreach (var t in _tweaks) { t.Refresh(); t.Status = "Restaurado."; }
        if (restart) await Dialog.Show(this, "RESET OTIMIZA", "Alguns ajustes só voltam ao original depois de reiniciar o PC.");
    }

    // ================= Inicialização =================

    private void ScanStartup()
    {
        List<StartupEntry> list;
        try { list = StartupManager.List(); }
        catch (Exception ex) { StartupResult.Text = $"Não foi possível listar: {ex.Message}"; return; }

        StartupList.ItemsSource = list;
        int on = list.Count(e => e.Enabled);
        StartupSummary.Text = list.Count == 0 ? "NENHUM PROGRAMA NA INICIALIZAÇÃO"
                            : $"{list.Count} PROGRAMAS · {on} ATIVADOS · {list.Count - on} DESATIVADOS";
        BtnStartupRestore.IsEnabled = _settings.StartupRemoved.Count > 0;
        BtnStartupRestore.Content = _settings.StartupRemoved.Count > 0
            ? $"RESTAURAR REMOVIDOS ({_settings.StartupRemoved.Count})" : "RESTAURAR REMOVIDOS";
    }

    private void BtnStartupScan_Click(object? sender, RoutedEventArgs e)
    {
        StartupResult.Text = "";
        ScanStartup();
    }

    private async void StartupToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupEntry item } box) return;
        bool turnOn = box.IsChecked == true;
        if (item.Machine && !await RequireAdmin()) { box.IsChecked = item.Enabled; return; }
        try
        {
            StartupManager.SetEnabled(item, turnOn);
            StartupResult.Text = $"{item.Name}: {(turnOn ? "ativado" : "desativado")}. Vale na próxima inicialização do Windows.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: falhou ({ex.Message})."; }
        ScanStartup();
    }

    private async void StartupRemove_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not StartupEntry item) return;
        if (item.Machine && !await RequireAdmin()) return;
        if (!await Dialog.Ask(this, "Inicialização", $"Remover \"{item.Name}\" da inicialização do Windows?\n\n{item.Command}\n\n" +
                                                     "O programa continua instalado, só deixa de abrir sozinho. " +
                                                     "Dá para desfazer em \"Restaurar removidos\".\n\n" +
                                                     "Se só quiser testar, prefira desligar o interruptor.")) return;
        try
        {
            _settings.StartupRemoved.Add(StartupManager.Remove(item));
            _settings.Save();
            StartupResult.Text = $"{item.Name} removido da inicialização.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: não foi possível remover ({ex.Message})."; }
        ScanStartup();
    }

    private async void BtnStartupRestore_Click(object? sender, RoutedEventArgs e)
    {
        var removed = _settings.StartupRemoved;
        if (removed.Count == 0) return;
        if (removed.Any(b => b.Machine) && !await RequireAdmin()) return;
        string names = string.Join("\n", removed.Take(12).Select(b => $"  • {b.Name}"))
                       + (removed.Count > 12 ? $"\n  … e mais {removed.Count - 12}" : "");
        if (!await Dialog.Ask(this, "Inicialização", $"Devolver {removed.Count} programas à inicialização do Windows?\n\n{names}")) return;

        var failed = new List<string>();
        foreach (var b in removed.ToList())
        {
            try { StartupManager.Restore(b); removed.Remove(b); }
            catch (Exception ex) { failed.Add($"{b.Name} ({ex.Message})"); }
        }
        _settings.Save();
        StartupResult.Text = failed.Count == 0 ? "Programas restaurados." : $"Não restaurados: {string.Join("; ", failed)}";
        ScanStartup();
    }

    // ================= Instalar apps (winget) =================

    private List<InstallableApp>? _installApps;
    private bool _appsBusy, _appsStop;

    private void InitApps()
    {
        if (_appsBusy) return;
        if (_installApps == null)
        {
            _installApps = AppInstaller.Catalog();
            AppsList.ItemsSource = _installApps;
            AppInstaller.RefreshInstalled(_installApps);
            foreach (var a in _installApps) a.Selected = a.PreSelect && !a.Installed;
        }
        else AppInstaller.RefreshInstalled(_installApps);

        bool winget = AppInstaller.WingetAvailable;
        BtnAppsStore.IsVisible = !winget;
        BtnAppsInstall.IsEnabled = winget;
        int installed = _installApps.Count(a => a.Installed);
        AppsStatus.Text = winget
            ? $"{installed} de {_installApps.Count} já instalados. Marque o que quer e clique em INSTALAR SELECIONADOS."
            : "O winget não foi encontrado. Em PC recém-formatado ele aparece depois que a Microsoft Store atualiza o " +
              "\"Instalador de Aplicativo\": use o botão abaixo, espere atualizar e abra esta página de novo.";
    }

    private void BtnAppsAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_installApps == null || _appsBusy) return;
        foreach (var a in _installApps) a.Selected = !a.Installed;
    }

    private void BtnAppsNone_Click(object? sender, RoutedEventArgs e)
    {
        if (_installApps == null || _appsBusy) return;
        foreach (var a in _installApps) a.Selected = false;
    }

    // Página do "Instalador de Aplicativo" (App Installer), que traz o winget.
    private void BtnAppsStore_Click(object? sender, RoutedEventArgs e) => OpenUrl("ms-windows-store://pdp/?productid=9NBLGGH4NNS1");

    private async void BtnAppsInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_installApps == null) return;
        if (_appsBusy) { _appsStop = true; BtnAppsInstall.IsEnabled = false; AppsStatus.Text = "Parando depois do app atual…"; return; }

        var sel = _installApps.Where(a => a.Selected && !a.Installed).ToList();
        if (sel.Count == 0) { AppsStatus.Text = "Nenhum app selecionado."; return; }
        if (sel.Any(a => a.WingetId == "RARLab.WinRAR") && sel.Any(a => a.WingetId == "7zip.7zip") &&
            !await Dialog.Ask(this, "Instalar apps", "WinRAR e 7-Zip fazem a mesma coisa. Instalar os dois mesmo assim?")) return;

        _appsBusy = true; _appsStop = false;
        BtnAppsInstall.Content = "PARAR";
        foreach (var a in sel) a.Status = "Na fila.";
        int ok = 0, done = 0;
        var failed = new List<string>();
        AppsProgress.Value = 0;

        foreach (var app in sel)
        {
            if (_appsStop) { app.Status = "Não instalado (parado)."; continue; }
            AppsStatus.Text = $"Instalando {app.Name} ({done + 1} de {sel.Count})… baixando do site oficial, pode levar alguns minutos.";
            app.Status = "Instalando…";
            try
            {
                var (success, msg) = await AppInstaller.InstallAsync(app, CancellationToken.None);
                app.Status = msg;
                if (success) { ok++; app.Selected = false; }
                else failed.Add(app.Name);
            }
            catch (Exception ex) { app.Status = $"Falhou: {ex.Message}"; failed.Add(app.Name); }
            done++;
            AppsProgress.Value = (double)done / sel.Count;
        }

        AppInstaller.RefreshInstalled(_installApps);
        _appsBusy = false;
        BtnAppsInstall.Content = "INSTALAR SELECIONADOS";
        BtnAppsInstall.IsEnabled = true;
        AppsStatus.Text = $"{ok} de {sel.Count} instalados." +
                          (failed.Count > 0 ? $" Falharam: {string.Join(", ", failed)} (veja o motivo na lista)." : "") +
                          (_appsStop ? " Instalação interrompida." : "");
    }

    // ================= RESET DRIVER =================

    private bool _drvInit, _drvBusy;
    private List<GpuDriverStatus> _drvGpus = new();
    private BoardInfo _drvBoardInfo = new();
    private List<WuDriver> _drvWu = new();
    private CancellationTokenSource? _drvCts;

    private void InitDriver()
    {
        if (_drvInit) return;
        _drvInit = true;
        BtnDrvScan_Click(this, new RoutedEventArgs());
    }

    private void DrvSet(string text, double? frac = null)
    {
        DrvStatus.Text = text;
        if (frac.HasValue) DrvProgress.Value = Math.Clamp(frac.Value, 0, 1);
    }

    private void DrvBusy(bool busy)
    {
        _drvBusy = busy;
        BtnDrvScan.IsEnabled = BtnDrvBackup.IsEnabled = BtnDrvWuSearch.IsEnabled = !busy;
        BtnDrvInstall.IsEnabled = !busy && _drvGpus.Any(g => g.CanAutoInstall);
        BtnDrvWuInstall.IsEnabled = !busy && _drvWu.Count > 0;
    }

    private async void BtnDrvScan_Click(object? sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        DrvBusy(true);
        _drvCts = new CancellationTokenSource();
        try
        {
            DrvSet("Lendo drivers instalados…", 0.1);
            DrvList.ItemsSource = await Task.Run(DriverManager.Inventory);

            DrvSet("Lendo placa de vídeo e placa-mãe…", 0.3);
            _drvGpus = await Task.Run(DriverManager.ReadGpus);
            _drvBoardInfo = await Task.Run(DriverManager.ReadBoard);
            ShowBoard();

            bool studio = DrvStudio.IsChecked == true;
            int i = 0;
            foreach (var g in _drvGpus)
            {
                DrvSet($"Consultando {g.Vendor.ToUpperInvariant()} para {g.GpuName}…", 0.4 + 0.5 * i / Math.Max(1, _drvGpus.Count));
                if (g.Vendor == "NVIDIA") await DriverManager.CheckNvidiaAsync(g, studio, _drvCts.Token);
                else if (g.Vendor == "AMD") await DriverManager.CheckAmdAsync(g, _drvCts.Token);
                else if (g.Vendor == "Intel") g.Message = $"{g.Vendor}: use a página oficial para ver a versão mais recente (instalado: {g.Installed}).";
                else g.Message = "Fabricante não reconhecido.";
                i++;
            }
            DrvGpus.ItemsSource = null;
            DrvGpus.ItemsSource = _drvGpus;
            DrvSet($"Pronto às {DateTime.Now:HH:mm:ss}.", 1);
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private void ShowBoard()
    {
        var b = _drvBoardInfo;
        DrvBoard.Text = $"{b.Maker} {b.Model}\nBIOS: {(b.Bios.Length > 0 ? b.Bios : "—")}" +
                        (b.Brand.Length > 0 ? $"\nFabricante reconhecido: {b.Brand}" : "\nFabricante não reconhecido");
        BtnDrvBoardPage.IsEnabled = b.SupportUrl.Length > 0;
    }

    private void BtnDrvGpuPage_Click(object? sender, RoutedEventArgs e)
    {
        var g = _drvGpus.FirstOrDefault(x => x.OfficialPage.Length > 0 && x.Vendor != "Intel") ?? _drvGpus.FirstOrDefault(x => x.OfficialPage.Length > 0);
        OpenUrl(g?.OfficialPage ?? "https://www.nvidia.com/Download/index.aspx");
    }

    private void BtnDrvBoardPage_Click(object? sender, RoutedEventArgs e) => OpenUrl(_drvBoardInfo.SupportUrl);

    private async void BtnDrvInstall_Click(object? sender, RoutedEventArgs e)
    {
        var g = _drvGpus.FirstOrDefault(x => x.CanAutoInstall);
        if (g == null || _drvBusy) return;
        string msg = g.Vendor == "AMD"
            ? $"Baixar e abrir o instalador AMD Software {g.Latest}?\nO arquivo vem do servidor oficial da AMD."
            : g.UpdateAvailable
            ? $"Instalar o driver NVIDIA {g.Latest} ({g.SizeText})?\nO instalador oficial será baixado e aberto."
            : $"Você já está com a versão {g.Installed}. Baixar e abrir o instalador {g.Latest} mesmo assim (reinstalação)?";
        if (!await Dialog.Ask(this, "RESET DRIVER", msg)) return;

        DrvBusy(true);
        _drvCts = new CancellationTokenSource();
        try
        {
            string r = await DriverManager.DownloadAndLaunchAsync(g, (t, f) => Dispatcher.UIThread.Post(() => DrvSet(t, f)), _drvCts.Token);
            DrvSet(r, 1);
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private async void BtnDrvWuSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        DrvBusy(true);
        DrvSet("Procurando drivers no Windows Update (pode levar 1 minuto)…", 0.3);
        try
        {
            _drvWu = await DriverManager.SearchWindowsUpdateAsync();
            DrvWuList.Text = _drvWu.Count == 0
                ? "Nenhum driver novo no Windows Update."
                : string.Join("\n", _drvWu.Select(w => "• " + w.Title + (w.SizeBytes > 0 ? $" ({w.SizeBytes / 1048576.0:F0} MB)" : "")));
            DrvSet($"{_drvWu.Count} driver(s) encontrados.", 1);
        }
        catch (Exception ex) { DrvWuList.Text = "Falhou: " + ex.Message; DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private async void BtnDrvWuInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_drvBusy || _drvWu.Count == 0) return;
        if (!await Dialog.Ask(this, "RESET DRIVER", $"Instalar {_drvWu.Count} driver(s) do Windows Update? Pode ser preciso reiniciar.")) return;
        DrvBusy(true);
        try
        {
            string r = await DriverManager.InstallWindowsUpdateAsync(_drvWu, t => Dispatcher.UIThread.Post(() => DrvSet(t, 0.5)));
            DrvSet(r, 1);
            _drvWu = new();
            DrvWuList.Text = r;
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }

    private async void BtnDrvBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        DrvBusy(true);
        _drvCts = new CancellationTokenSource();
        try
        {
            string r = await DriverManager.BackupAsync(t => Dispatcher.UIThread.Post(() => DrvSet(t, 0.5)), _drvCts.Token);
            DrvSet(r, 1);
        }
        catch (Exception ex) { DrvSet("Falhou: " + ex.Message, 0); }
        finally { DrvBusy(false); }
    }
#else
    // TODO(Fase 5): Instalar apps (Flatpak/apt/dnf) e Drivers no Linux.
    private const string NotYet = "Ainda não disponível neste sistema.";
    // ================= RESET OTIMIZA (Linux) =================

    private List<LinuxApp> _apps = new();
    private List<LinuxTweak>? _linuxTweaks;

    private void InitOptimize()
    {
        if (_linuxTweaks == null)
        {
            OptSubtitle.Text = "Libera memória, fecha o que atrapalha e ajusta o Linux para jogos";
            OptHint.Text = "Um clique: esvazia o cache de memória do sistema (pede a senha) e fecha os programas marcados na lista abaixo. Use antes de abrir um jogo.";
            OptTweaksLabel.Text = "AJUSTES DO LINUX PARA JOGOS";
            OptTweaksHint.Text = "O interruptor mostra o estado atual e aplica na hora; os que dizem PEDE SENHA abrem a janela de senha do sistema. " +
                                 "O valor original é guardado na primeira alteração. Não mexemos em segurança (firewall, SELinux/AppArmor, atualizações).";
            _linuxTweaks = LinuxTweaks.All();
            foreach (var t in _linuxTweaks)
            {
                try { t.Refresh(); } catch (Exception ex) { t.Status = $"Não foi possível ler: {ex.Message}"; }
                if (t.Unavailable && t.Status.Length == 0)
                    t.Status = t.Id == "power" ? "Indisponível: falta o power-profiles-daemon (ou tuned-ppd)." : "Indisponível neste kernel.";
            }
            OptTweaks.ItemsSource = _linuxTweaks;
        }
        RefreshApps();
        UpdateRamStatus();
    }

    private async void RefreshApps()
    {
        _apps = await Task.Run(LinuxOptimizer.ListApps);
        OptApps.ItemsSource = _apps;
    }

    private void BtnRefreshApps_Click(object? sender, RoutedEventArgs e) => RefreshApps();

    private async void BtnOptimize_Click(object? sender, RoutedEventArgs e)
    {
        var toClose = _apps.Where(a => a.Selected).ToList();
        string list = toClose.Count == 0 ? "nenhum programa marcado" : string.Join("\n", toClose.Select(a => $"  • {a.Title}"));
        if (!await Dialog.Ask(this, "RESET OTIMIZA", "O RESET OTIMIZA vai:\n\n" +
                                                     "1. Esvaziar o cache de memória do sistema (o sistema vai pedir sua senha).\n" +
                                                     $"2. Fechar os programas marcados:\n{list}\n\n" +
                                                     "Salve o que estiver aberto nesses programas antes. Continuar?")) return;

        BtnOptimize.IsEnabled = false;
        BtnOptimize.Content = "OTIMIZANDO…";
        OptResult.Text = "";
        var (closed, notClosed) = await Task.Run(() => LinuxOptimizer.CloseApps(toClose));
        var mem = await LinuxOptimizer.CleanMemoryAsync(); // depois de fechar, para contar a memória liberada por eles também
        BtnOptimize.IsEnabled = true;
        BtnOptimize.Content = "RESET OTIMIZA";

        var parts = new List<string> { $"Memória livre a mais: {mem.FreedBytes / 1048576.0:N0} MB" };
        parts.Add(mem.CacheDropped ? "cache do sistema esvaziado" : $"cache não esvaziado ({mem.Error})");
        if (closed.Count > 0) parts.Add($"fechados: {string.Join(", ", closed)}");
        if (notClosed.Count > 0) parts.Add($"não fecharam (podem ter algo não salvo ou pedir confirmação): {string.Join(", ", notClosed)}");
        OptResult.Text = string.Join(" · ", parts) + ".";
        RefreshApps();
        UpdateRamStatus();
    }

    private async void Tweak_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: LinuxTweak t } box) return;
        bool turnOn = box.IsChecked == true;
        box.IsEnabled = false;
        t.Status = t.NeedsRoot ? "Aguardando a senha…" : "Aplicando…";
        try
        {
            string? error = await t.WriteAsync(turnOn, _settings);
            _settings.Save();
            t.Refresh();
            t.Status = error ?? (t.IsOn == turnOn ? (turnOn ? "Aplicado." : "Desligado.") : "O sistema não aceitou a mudança.");
        }
        catch (Exception ex)
        {
            t.Refresh();
            t.Status = $"Falhou: {ex.Message}";
        }
        box.IsChecked = t.IsOn; // o binding é só leitura: volta ao estado real
        box.IsEnabled = true;
    }

    private async void BtnRestoreTweaks_Click(object? sender, RoutedEventArgs e)
    {
        if (_linuxTweaks == null) return;
        if (!_settings.TweakBackup.Keys.Any(k => k.StartsWith("linux.")))
        {
            await Dialog.Show(this, "RESET OTIMIZA", "Nenhum ajuste foi alterado pelo ABExplode ainda.");
            return;
        }
        if (!await Dialog.Ask(this, "RESET OTIMIZA", "Voltar todos os ajustes do Linux para como estavam antes do ABExplode alterá-los?")) return;
        var errors = await LinuxTweaks.RestoreAllAsync(_linuxTweaks, _settings);
        _settings.Save();
        foreach (var t in _linuxTweaks) { t.Refresh(); t.Status = errors.Count == 0 ? "Restaurado." : ""; }
        if (errors.Count > 0) await Dialog.Show(this, "RESET OTIMIZA", "Alguns ajustes não voltaram:\n\n" + string.Join("\n", errors));
    }

    // ================= Inicialização (Linux) =================

    private void ScanStartup()
    {
        StartupSubtitle.Text = "Programas que abrem junto com a sessão · desative ou remova o que não precisa";
        StartupHint.Text = "O interruptor ativa ou desativa na hora, como as configurações de inicialização do GNOME e do KDE (a entrada continua lá " +
                           "e pode ser religada). REMOVER tira o item da inicialização; o programa continua instalado e dá para desfazer em " +
                           "\"Restaurar removidos\". Vale na próxima vez que você entrar na sessão. Nada aqui pede senha.";
        List<LinuxStartupEntry> list;
        try { list = LinuxAutostart.List(); }
        catch (Exception ex) { StartupResult.Text = $"Não foi possível listar: {ex.Message}"; return; }

        StartupList.ItemsSource = list;
        int on = list.Count(e => e.Enabled);
        StartupSummary.Text = list.Count == 0 ? "NENHUM PROGRAMA NA INICIALIZAÇÃO"
                            : $"{list.Count} PROGRAMAS · {on} ATIVADOS · {list.Count - on} DESATIVADOS";
        BtnStartupRestore.IsEnabled = _settings.StartupRemoved.Count > 0;
        BtnStartupRestore.Content = _settings.StartupRemoved.Count > 0
            ? $"RESTAURAR REMOVIDOS ({_settings.StartupRemoved.Count})" : "RESTAURAR REMOVIDOS";
    }

    private void BtnStartupScan_Click(object? sender, RoutedEventArgs e)
    {
        StartupResult.Text = "";
        ScanStartup();
    }

    private void StartupToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: LinuxStartupEntry item }) return;
        bool turnOn = (sender as CheckBox)!.IsChecked == true;
        try
        {
            LinuxAutostart.SetEnabled(item, turnOn);
            StartupResult.Text = $"{item.Name}: {(turnOn ? "ativado" : "desativado")}. Vale na próxima vez que você entrar na sessão.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: falhou ({ex.Message})."; }
        ScanStartup();
    }

    private async void StartupRemove_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not LinuxStartupEntry item) return;
        if (!await Dialog.Ask(this, "Inicialização", $"Remover \"{item.Name}\" da inicialização?\n\n{item.Command}\n\n" +
                                                     "O programa continua instalado, só deixa de abrir sozinho. " +
                                                     "Dá para desfazer em \"Restaurar removidos\".\n\n" +
                                                     "Se só quiser testar, prefira desligar o interruptor.")) return;
        try
        {
            _settings.StartupRemoved.Add(LinuxAutostart.Remove(item));
            _settings.Save();
            StartupResult.Text = $"{item.Name} removido da inicialização.";
        }
        catch (Exception ex) { StartupResult.Text = $"{item.Name}: não foi possível remover ({ex.Message})."; }
        ScanStartup();
    }

    private async void BtnStartupRestore_Click(object? sender, RoutedEventArgs e)
    {
        var removed = _settings.StartupRemoved;
        if (removed.Count == 0) return;
        string names = string.Join("\n", removed.Take(12).Select(b => $"  • {b.Name}"))
                       + (removed.Count > 12 ? $"\n  … e mais {removed.Count - 12}" : "");
        if (!await Dialog.Ask(this, "Inicialização", $"Devolver {removed.Count} programas à inicialização?\n\n{names}")) return;

        var failed = new List<string>();
        foreach (var b in removed.ToList())
        {
            try { LinuxAutostart.Restore(b); removed.Remove(b); }
            catch (Exception ex) { failed.Add($"{b.Name} ({ex.Message})"); }
        }
        _settings.Save();
        StartupResult.Text = failed.Count == 0 ? "Programas restaurados." : $"Não restaurados: {string.Join("; ", failed)}";
        ScanStartup();
    }
    private void InitApps() { AppsStatus.Text = NotYet; BtnAppsInstall.IsEnabled = false; }
    private void BtnAppsAll_Click(object? sender, RoutedEventArgs e) { }
    private void BtnAppsNone_Click(object? sender, RoutedEventArgs e) { }
    private void BtnAppsStore_Click(object? sender, RoutedEventArgs e) { }
    private void BtnAppsInstall_Click(object? sender, RoutedEventArgs e) => AppsStatus.Text = NotYet;
    private void InitDriver() => DrvStatus.Text = NotYet;
    private void BtnDrvScan_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
    private void BtnDrvGpuPage_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
    private void BtnDrvBoardPage_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
    private void BtnDrvInstall_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
    private void BtnDrvWuSearch_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
    private void BtnDrvWuInstall_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
    private void BtnDrvBackup_Click(object? sender, RoutedEventArgs e) => DrvStatus.Text = NotYet;
#endif
}
