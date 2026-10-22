using Avalonia.Controls;
using Avalonia.Interactivity;
#if WINDOWS
using ABExplode.Platform.Windows;
#else
using ABExplode.Platform.Linux;
#endif

namespace ABExplode.App;

/// <summary>
/// RESET MAKER: Portas COM (registros fantasmas e números COM presos) e Drivers seriais (CH340, CP210x, FTDI, Prolific,
/// Arduino e Arduino IDE). No Windows usa SetupAPI, ComDB e pnputil (UsbCleaner, MakerDrivers); no Linux (LinuxMaker) não há
/// fantasmas nem drivers para instalar: o diagnóstico cobre as portas /dev/tty*, o grupo dialout/uucp e o brltty.
/// </summary>
public partial class MainWindow
{
#if WINDOWS
    // ================= Portas COM =================

    private List<UsbDevice> _usb = new();

    private void ScanUsb()
    {
        try
        {
            _usb = UsbCleaner.List();
        }
        catch (Exception ex) { UsbResult.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; return; }

        static bool Relevant(UsbDevice d) => d.Port != null || d.Maker != null;
        var connected = _usb.Where(d => d.Present && Relevant(d)).ToList();
        UsbConnected.Text = connected.Count == 0 ? "Nenhuma placa ou porta serial conectada."
                          : string.Join("\n", connected.Select(d => $"{d.Port ?? "—"} · {d.Name}"));

        var shown = _usb.Where(d => !d.Present && (UsbShowAll.IsChecked == true || Relevant(d))).ToList();
        UsbList.ItemsSource = shown;
        int ghosts = _usb.Count(d => !d.Present);
        UsbSummary.Text = shown.Count == 0
            ? "Nenhum registro antigo encontrado."
            : $"{shown.Count} registros antigos na lista ({ghosts} dispositivos ausentes no total).";

        try
        {
            var (reserved, inUse) = UsbCleaner.ComStatus();
            string R(IEnumerable<int> l) => l.Any() ? string.Join(", ", l.Select(n => $"COM{n}")) : "nenhum";
            UsbComDb.Text = $"{reserved.Count} reservados: {R(reserved)}\nEm uso agora: {R(inUse)}";
        }
        catch (Exception ex) { UsbComDb.Text = ex.Message; }
        BtnComDbRestore.IsEnabled = _settings.ComDbBackup != null;
    }

    private void BtnUsbScan_Click(object? sender, RoutedEventArgs e) => ScanUsb();
    private void UsbShowAll_Changed(object? sender, RoutedEventArgs e) { if (UsbList != null) ScanUsb(); }

    private async void BtnUsbRemove_Click(object? sender, RoutedEventArgs e)
    {
        var sel = _usb.Where(d => d.Selected && !d.Present).ToList();
        if (sel.Count == 0) { UsbResult.Text = "Nenhum registro selecionado."; return; }
        if (!await RequireAdmin()) return;
        string names = string.Join("\n", sel.Take(12).Select(d => $"  • {d.Name}{(d.Port != null ? "" : $" ({d.Class})")}"))
                       + (sel.Count > 12 ? $"\n  … e mais {sel.Count - 12}" : "");
        if (!await Dialog.Ask(this, "Maker", $"Remover {sel.Count} registros de dispositivos que não estão conectados?\n\n{names}\n\n" +
                                                   "Se plugar algum deles de novo, o Windows reinstala automaticamente.")) return;

        BtnUsbRemove.IsEnabled = false;
        UsbResult.Text = "Removendo…";
        var (removed, failed) = await Task.Run(() => UsbCleaner.Remove(sel.Select(d => d.InstanceId)));
        BtnUsbRemove.IsEnabled = true;
        UsbResult.Text = $"{removed} registros removidos." +
                         (failed.Count > 0 ? $" Não removidos: {string.Join("; ", failed.Take(5))}" : "") +
                         " Para liberar os números COM que eles prendiam, use \"Liberar números COM presos\".";
        ScanUsb();
    }

    private async void BtnComDb_Click(object? sender, RoutedEventArgs e)
    {
        if (!await RequireAdmin()) return;
        var (reserved, inUse) = UsbCleaner.ComStatus();
        var freed = reserved.Except(inUse).ToList();
        if (freed.Count == 0) { UsbResult.Text = "Nenhum número COM preso: todos os reservados estão em uso."; return; }
        if (!await Dialog.Ask(this, "Maker", $"Liberar {freed.Count} números COM presos por dispositivos que não estão conectados?\n\n" +
                                                   $"Liberados: {string.Join(", ", freed.Select(n => $"COM{n}"))}\n" +
                                                   $"Mantidos (em uso): {(inUse.Count > 0 ? string.Join(", ", inUse.Select(n => $"COM{n}")) : "nenhum")}\n\n" +
                                                   "A próxima placa plugada vai receber um número baixo (COM3, COM4…). " +
                                                   "O valor atual fica guardado para \"Desfazer liberação\".")) return;
        try
        {
            var old = UsbCleaner.RebuildComDb();
            _settings.ComDbBackup ??= old; // guarda o original da primeira vez
            _settings.Save();
            UsbResult.Text = $"Liberados: {string.Join(", ", freed.Select(n => $"COM{n}"))}. Plugue a placa de novo para ela pegar um número baixo.";
        }
        catch (Exception ex) { UsbResult.Text = $"Falhou: {ex.Message}"; }
        ScanUsb();
    }

    private async void BtnComDbRestore_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings.ComDbBackup == null || !await RequireAdmin()) return;
        try
        {
            UsbCleaner.RestoreComDb(_settings.ComDbBackup);
            _settings.ComDbBackup = null;
            _settings.Save();
            UsbResult.Text = "Reservas de números COM restauradas para como estavam.";
        }
        catch (Exception ex) { UsbResult.Text = $"Falhou: {ex.Message}"; }
        ScanUsb();
    }

    // ================= Drivers seriais =================

    private bool _makerInit, _makerBusy;
    private List<SerialDeviceRow> _makerDevices = new();

    private void InitMakerDrivers()
    {
        if (_makerInit) { RefreshMakerDevices(); return; }
        _makerInit = true;
        BtnMakerScan_Click(this, new RoutedEventArgs());
    }

    private void MakerBusy(bool busy, string? status = null)
    {
        _makerBusy = busy;
        BtnMakerScan.IsEnabled = BtnMakerBundleAll.IsEnabled = BtnMakerIde.IsEnabled = !busy;
        if (status != null) MakerStatus.Text = status;
    }

    private async void BtnMakerScan_Click(object? sender, RoutedEventArgs e)
    {
        if (_makerBusy) return;
        MakerBusy(true, "Verificando placas, conversores e drivers…");
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        RefreshMakerIde();
        MakerBusy(false);
        int bad = _makerDevices.Count(d => !d.Ok);
        MakerStatus.Text = _makerDevices.Count == 0 ? "Nenhuma placa conectada agora. Os drivers instalados aparecem abaixo."
                         : bad == 0 ? $"{_makerDevices.Count} conectados, todos funcionando."
                         : $"{bad} de {_makerDevices.Count} com problema: veja a orientação em cada um.";
    }

    private void RefreshMakerDevices()
    {
        try { _makerDevices = MakerDrivers.ConnectedDevices(); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; _makerDevices = new(); }
        MakerDevices.ItemsSource = _makerDevices;
        MakerNoDevices.IsVisible = _makerDevices.Count == 0;
    }

    private async Task RefreshMakerPackagesAsync()
    {
        List<SerialDriverPackage> pkgs;
        try { pkgs = await MakerDrivers.DriverPackagesAsync(_makerDevices); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível ler os drivers: {ex.Message}"; pkgs = new(); }
        MakerPackages.ItemsSource = pkgs;
        MakerNoPackages.IsVisible = pkgs.Count == 0;
        MakerBundled.ItemsSource = MakerDrivers.Bundled(pkgs);
        BtnMakerRestore.IsEnabled = MakerDrivers.HasBackups;
    }

    private void RefreshMakerIde()
    {
        string? v = MakerDrivers.IdeVersion();
        MakerIde.Text = v != null ? $"Arduino IDE {v} instalada{(MakerDrivers.IdeRunning ? " · aberta agora" : "")}." : "Arduino IDE não instalada.";
        BtnMakerIde.Content = v != null ? "ATUALIZAR A IDE" : "INSTALAR A IDE";
        var holders = MakerDrivers.RunningPortHolders();
        MakerHolders.Text = holders.Count == 0 ? "Nenhum programa conhecido prendendo portas COM agora."
                          : "Podem estar prendendo a porta: " + string.Join(", ", holders.Select(p => p.ProcessName).Distinct()) + ".";
    }

    private async void MakerReinstall_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not SerialDeviceRow row || _makerBusy) return;
        if (!await RequireAdmin()) return;
        MakerBusy(true, $"Reinstalando {row.Name}…");
        try { MakerStatus.Text = await MakerDrivers.ReinstallAsync(row.Device); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1500); // dá tempo do Windows terminar a instalação antes de reler
        RefreshMakerDevices();
        MakerBusy(false);
    }

    private void MakerOfficial_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SerialChip chip) OpenUrl(chip.OfficialUrl);
    }

    private async void MakerInstallBundled_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not BundledDriver b || _makerBusy) return;
        if (!await RequireAdmin()) return;
        await InstallBundledAsync(new[] { b.Chip });
    }

    private async void BtnMakerBundleAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_makerBusy || !await RequireAdmin()) return;
        var chips = MakerDrivers.Chips.Where(c => c.Bundle != null).ToList();
        await InstallBundledAsync(chips);
    }

    private async Task InstallBundledAsync(IReadOnlyList<SerialChip> chips)
    {
        var results = new List<string>();
        foreach (var c in chips)
        {
            MakerBusy(true, $"Instalando o driver do {c.Name}…");
            try { results.Add(await MakerDrivers.InstallBundledAsync(c)); }
            catch (Exception ex) { results.Add($"{c.Name}: falhou ({ex.Message})"); }
        }
        await Task.Delay(1000);
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        MakerBusy(false, string.Join(" ", results.Where(r => !r.Contains("não está incluído"))));
    }

    private async void MakerRemovePackage_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not SerialDriverPackage p || _makerBusy) return;
        if (!await RequireAdmin()) return;
        if (!await Dialog.Ask(this, "Maker", $"Remover o driver {p.Title} ({p.PublishedName}) do Windows?\n\n" +
                                                   (p.InUse ? "Ele está EM USO por uma placa conectada: o Windows troca para outra versão guardada, " +
                                                              "ou a placa fica sem driver se esta for a única.\n\n" : "") +
                                                   "Um backup é feito antes; dá para desfazer em \"Restaurar removidos\".")) return;
        MakerBusy(true, $"Removendo {p.Title}…");
        try { MakerStatus.Text = await MakerDrivers.RemovePackageAsync(p); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1000);
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        MakerBusy(false);
    }

    private async void BtnMakerRestore_Click(object? sender, RoutedEventArgs e)
    {
        if (_makerBusy || !await RequireAdmin()) return;
        if (!await Dialog.Ask(this, "Maker", "Devolver ao Windows todos os drivers seriais removidos pelo ABExplode?")) return;
        MakerBusy(true, "Restaurando drivers…");
        try { MakerStatus.Text = await MakerDrivers.RestoreBackupsAsync(); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        RefreshMakerDevices();
        await RefreshMakerPackagesAsync();
        MakerBusy(false);
    }

    private async void BtnMakerIde_Click(object? sender, RoutedEventArgs e)
    {
        if (_makerBusy) return;
        if (!AppInstaller.WingetAvailable)
        {
            MakerIdeResult.Text = "O winget não foi encontrado: abra FERRAMENTAS › Instalar apps para ver como resolver, ou baixe a IDE no site.";
            return;
        }
        if (MakerDrivers.IdeRunning)
        {
            MakerIdeResult.Text = "Feche a Arduino IDE antes (salve o sketch) e clique de novo.";
            return;
        }
        bool installed = MakerDrivers.IdeVersion() != null;
        var app = new InstallableApp { Name = "Arduino IDE", WingetId = "ArduinoSA.IDE.stable" };
        MakerBusy(true);
        MakerIdeResult.Text = installed ? "Atualizando a Arduino IDE… (pode levar alguns minutos)" : "Instalando a Arduino IDE… (pode levar alguns minutos)";
        try
        {
            var (ok, msg) = await AppInstaller.InstallAsync(app, CancellationToken.None, upgrade: installed);
            // "upgrade" sem versão nova responde com o código de "nada para atualizar"
            MakerIdeResult.Text = installed && ok && msg != "Instalado." ? "A IDE já está na versão mais recente." : msg;
        }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
        RefreshMakerIde();
        MakerBusy(false);
    }

    private async void BtnMakerHolders_Click(object? sender, RoutedEventArgs e)
    {
        var procs = MakerDrivers.RunningPortHolders();
        if (procs.Count == 0) { MakerIdeResult.Text = "Nenhum programa conhecido prendendo portas COM."; RefreshMakerIde(); return; }
        var names = procs.Select(p => p.ProcessName).Distinct().ToList();
        if (!await Dialog.Ask(this, "Maker", "Fechar estes programas? Trabalho não salvo neles pode ser perdido.\n\n" +
                                                   string.Join("\n", names.Select(n => "  • " + n)))) return;
        int closed = 0;
        foreach (var p in procs)
            try { p.Kill(entireProcessTree: true); closed++; } catch { }
        MakerIdeResult.Text = $"{closed} programas fechados. Tente o upload de novo.";
        RefreshMakerIde();
    }

    private void BtnMakerCache_Click(object? sender, RoutedEventArgs e)
    {
        if (MakerDrivers.IdeRunning) { MakerIdeResult.Text = "Feche a Arduino IDE antes de limpar o cache."; return; }
        try { MakerIdeResult.Text = MakerDrivers.ClearIdeCache(); }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
    }
#else
    // ================= Portas COM (Linux: /dev/ttyUSB*, /dev/ttyACM*, grupo dialout e brltty) =================

    private bool _portsTexts;

    private void ScanUsb()
    {
        if (!_portsTexts)
        {
            _portsTexts = true;
            PortsSubtitle.Text = "Arduino, ESP32 e conversores seriais · portas, permissão de acesso e o brltty";
            PortsCardLabel.Text = "PORTAS SERIAIS USB (ARDUINO, ESP32, CONVERSORES)";
            PortsHint.Text = "No Linux não há registros fantasmas nem números COM presos: cada placa vira /dev/ttyUSB0, /dev/ttyACM0... " +
                             "Os problemas de verdade são três: o usuário fora do grupo das portas seriais (a IDE diz \"Permission denied\"), " +
                             "o brltty pegando o CH340 (a porta nem aparece, comum no Ubuntu) e outro programa com a porta aberta.";
            PortsRightLabel.Text = "PERMISSÃO E CONFLITOS";
            UsbList.IsVisible = UsbShowAll.IsVisible = false;
        }
        List<LinuxSerialDevice> devices;
        SerialAccess acc;
        try { devices = LinuxMaker.ConnectedDevices(); acc = LinuxMaker.Access(); }
        catch (Exception ex) { UsbResult.Text = $"Não foi possível ler as portas: {ex.Message}"; return; }

        UsbConnected.Text = devices.Count == 0 ? "Nenhuma placa ou conversor serial conectado."
                          : string.Join("\n", devices.Select(d => $"{(d.Port.Length > 0 ? d.Port : "sem porta")} · {d.Name}"));
        var lines = new List<string>
        {
            acc.InGroup ? $"Seu usuário está no grupo {acc.Group}: pode abrir as portas."
            : acc.AddedButNeedsRelogin ? $"Você entrou no grupo {acc.Group}, mas falta sair e entrar na sessão para valer."
            : $"Seu usuário NÃO está no grupo {acc.Group}: a IDE não consegue abrir as portas (\"Permission denied\").",
        };
        if (acc.BrlttyActive) lines.Add("brltty ativo: pode pegar placas com CH340 e impedir que a porta apareça.");
        else if (acc.BrlttyMasked) lines.Add("brltty desativado pelo ABExplode.");
        UsbComDb.Text = string.Join("\n", lines);

        var problems = devices.Where(d => !d.Ok).ToList();
        UsbSummary.Text = devices.Count == 0 ? "Plugue a placa para ver o diagnóstico."
                        : problems.Count == 0 ? $"{devices.Count} conectados, todos funcionando."
                        : string.Join("\n", problems.Select(p => $"{p.Name}: {p.Verdict} {p.Advice}"));

        BtnUsbRemove.Content = $"ENTRAR NO GRUPO {acc.Group.ToUpperInvariant()}";
        BtnUsbRemove.IsEnabled = !acc.InGroup && !acc.AddedButNeedsRelogin;
        BtnComDb.Content = "DESATIVAR O BRLTTY";
        BtnComDb.IsVisible = acc.BrlttyActive;
        BtnComDbRestore.Content = "REATIVAR O BRLTTY";
        BtnComDbRestore.IsVisible = acc.BrlttyMasked;
        BtnComDbRestore.IsEnabled = true;
    }

    private void BtnUsbScan_Click(object? sender, RoutedEventArgs e) => ScanUsb();
    private void UsbShowAll_Changed(object? sender, RoutedEventArgs e) { }

    // No Linux este botão coloca o usuário no grupo das portas seriais.
    private async void BtnUsbRemove_Click(object? sender, RoutedEventArgs e)
    {
        var acc = LinuxMaker.Access();
        if (!await Dialog.Ask(this, "Maker", $"Colocar seu usuário no grupo {acc.Group}? Assim a Arduino IDE e outros programas " +
                                                   "conseguem abrir as portas seriais.\n\nO sistema vai pedir sua senha. Depois é preciso sair e entrar " +
                                                   "na sessão (ou reiniciar) para valer.")) return;
        BtnUsbRemove.IsEnabled = false;
        UsbResult.Text = await LinuxMaker.JoinGroupAsync();
        ScanUsb();
    }

    // No Linux: desativa o brltty.
    private async void BtnComDb_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialog.Ask(this, "Maker", "Desativar o brltty?\n\nEle é o leitor de tela para linhas braille e, no Ubuntu, pega placas com CH340 " +
                                                   "achando que são uma linha braille: a porta /dev/ttyUSB0 nem chega a aparecer. Desativar não desinstala " +
                                                   "nada e dá para reativar aqui. Se você usa uma linha braille, não desative.\n\nO sistema vai pedir sua senha.")) return;
        UsbResult.Text = await LinuxMaker.SetBrlttyAsync(disable: true);
        ScanUsb();
    }

    // No Linux: reativa o brltty.
    private async void BtnComDbRestore_Click(object? sender, RoutedEventArgs e)
    {
        UsbResult.Text = await LinuxMaker.SetBrlttyAsync(disable: false);
        ScanUsb();
    }

    // ================= Drivers seriais (Linux: módulos do kernel, diagnóstico e Arduino IDE) =================

    private bool _makerInit, _makerBusy;
    private List<LinuxSerialDevice> _makerDevices = new();

    private void InitMakerDrivers()
    {
        if (_makerInit) { RefreshMakerDevices(); return; }
        _makerInit = true;
        MakerSubtitle.Text = "CH340 · CP2102/CP210x · FTDI · Prolific · Arduino · ESP32 · Pico — diagnóstico de cada placa (os drivers vêm no kernel)";
        MakerDevicesHint.Text = "REINSTALAR desconecta e reconecta a placa pelo sistema, sem tirar o cabo (pede a senha): o kernel detecta de novo e " +
                                "escolhe o driver. Placa que não aparece nenhuma vez: troque o cabo, muitos são só de carga.";
        MakerBundledCard.IsVisible = false; // no Linux os drivers já vêm no kernel
        MakerPackagesLabel.Text = "MÓDULOS DO KERNEL";
        MakerPackagesHint.Text = "Os drivers dos chips seriais são módulos do kernel e carregam sozinhos ao plugar a placa. " +
                                 "Se um aparecer como \"não encontrado\", o kernel foi compilado sem ele (raro): atualize o sistema.";
        BtnMakerRestore.IsVisible = false;
        MakerPackages.IsVisible = false;
        BtnMakerScan_Click(this, new RoutedEventArgs());
    }

    private void MakerBusy(bool busy, string? status = null)
    {
        _makerBusy = busy;
        BtnMakerScan.IsEnabled = BtnMakerIde.IsEnabled = !busy;
        if (status != null) MakerStatus.Text = status;
    }

    private async void BtnMakerScan_Click(object? sender, RoutedEventArgs e)
    {
        if (_makerBusy) return;
        MakerBusy(true, "Verificando placas, conversores e drivers…");
        RefreshMakerDevices();
        var modules = await Task.Run(LinuxMaker.ModuleStatus);
        MakerNoPackages.Text = string.Join("\n", modules);
        MakerNoPackages.IsVisible = true;
        await RefreshMakerIde();
        MakerBusy(false);
        int bad = _makerDevices.Count(d => !d.Ok);
        MakerStatus.Text = _makerDevices.Count == 0 ? "Nenhuma placa conectada agora."
                         : bad == 0 ? $"{_makerDevices.Count} conectados, todos funcionando."
                         : $"{bad} de {_makerDevices.Count} com problema: veja a orientação em cada um.";
    }

    private void RefreshMakerDevices()
    {
        try { _makerDevices = LinuxMaker.ConnectedDevices(); }
        catch (Exception ex) { MakerStatus.Text = $"Não foi possível listar os dispositivos: {ex.Message}"; _makerDevices = new(); }
        MakerDevices.ItemsSource = _makerDevices;
        MakerNoDevices.IsVisible = _makerDevices.Count == 0;
    }

    private async Task RefreshMakerIde()
    {
        string? v = await Task.Run(LinuxMaker.IdeVersion);
        MakerIde.Text = v != null ? $"Arduino IDE {v} instalada." : "Arduino IDE não instalada.";
        BtnMakerIde.Content = v == null ? "INSTALAR A IDE" : v.EndsWith("(Flatpak)") ? "ATUALIZAR A IDE" : "IDE DA DISTRIBUIÇÃO";
        BtnMakerIde.IsEnabled = v == null || v.EndsWith("(Flatpak)");
        var holders = await Task.Run(LinuxMaker.PortHolderProcesses);
        MakerHolders.Text = holders.Count == 0 ? "Nenhum programa com porta serial aberta agora."
                          : "Com a porta aberta agora: " + string.Join(", ", holders.Select(h => h.Name).Distinct()) + ".";
    }

    private async void MakerReinstall_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not LinuxSerialDevice row || _makerBusy) return;
        MakerBusy(true, $"Reconectando {row.Name}… (o sistema vai pedir sua senha)");
        try { MakerStatus.Text = await LinuxMaker.ReconnectAsync(row); }
        catch (Exception ex) { MakerStatus.Text = $"Falhou: {ex.Message}"; }
        await Task.Delay(1500); // dá tempo do kernel terminar de detectar antes de reler
        RefreshMakerDevices();
        MakerBusy(false);
    }

    private void MakerOfficial_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is LinuxSerialChip chip) OpenUrl(chip.OfficialUrl);
    }

    private void MakerInstallBundled_Click(object? sender, RoutedEventArgs e) { } // cartão escondido no Linux
    private void BtnMakerBundleAll_Click(object? sender, RoutedEventArgs e) { }
    private void MakerRemovePackage_Click(object? sender, RoutedEventArgs e) { }
    private void BtnMakerRestore_Click(object? sender, RoutedEventArgs e) { }

    private async void BtnMakerIde_Click(object? sender, RoutedEventArgs e)
    {
        if (_makerBusy) return;
        if (!LinuxAppInstaller.FlatpakAvailable)
        {
            MakerIdeResult.Text = "O Flatpak não está instalado: abra FERRAMENTAS › Instalar apps para instalá-lo, ou instale a IDE pela distribuição.";
            return;
        }
        if (LinuxMaker.IdeRunning)
        {
            MakerIdeResult.Text = "Feche a Arduino IDE antes (salve o sketch) e clique de novo.";
            return;
        }
        bool installed = LinuxMaker.IdeVersion() != null;
        MakerBusy(true);
        MakerIdeResult.Text = installed ? "Atualizando a Arduino IDE… (pode levar alguns minutos)" : "Instalando a Arduino IDE pelo Flathub… (pode levar alguns minutos)";
        try
        {
            if (installed)
            {
                string? r = await Task.Run(() => ABExplodeLinuxUpdateIde());
                MakerIdeResult.Text = r ?? "Não foi possível atualizar a IDE.";
            }
            else
            {
                var app = new LinuxInstallableApp { Name = "Arduino IDE", FlatpakId = LinuxMaker.IdeFlatpak };
                var (_, msg) = await LinuxAppInstaller.InstallAsync(app, CancellationToken.None);
                MakerIdeResult.Text = msg + " Para a IDE abrir as portas, seu usuário precisa estar no grupo das portas seriais (tela Portas COM).";
            }
        }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
        await RefreshMakerIde();
        MakerBusy(false);
    }

    private static string? ABExplodeLinuxUpdateIde()
    {
        string? outp = LinuxAppInstaller.UpdateFlatpak(LinuxMaker.IdeFlatpak);
        return outp == null ? null : outp.Contains("Nothing to do", StringComparison.OrdinalIgnoreCase) ? "A IDE já está na versão mais recente." : "Arduino IDE atualizada.";
    }

    private async void BtnMakerHolders_Click(object? sender, RoutedEventArgs e)
    {
        // A IDE em si não é fechada: só o monitor serial, terminais e fatiadores que seguram a porta.
        var holders = LinuxMaker.PortHolderProcesses().Where(h => !h.Name.Equals("arduino-ide", StringComparison.OrdinalIgnoreCase)).ToList();
        if (holders.Count == 0) { MakerIdeResult.Text = "Nenhum programa com porta serial aberta."; await RefreshMakerIde(); return; }
        var names = holders.Select(h => h.Name).Distinct().ToList();
        if (!await Dialog.Ask(this, "Maker", "Fechar estes programas? Trabalho não salvo neles pode ser perdido.\n\n" +
                                                   string.Join("\n", names.Select(n => "  • " + n)))) return;
        int closed = LinuxMaker.CloseHolders(holders.Select(h => h.Pid));
        MakerIdeResult.Text = $"{closed} programas fechados. Tente o upload de novo.";
        await Task.Delay(500);
        await RefreshMakerIde();
    }

    private void BtnMakerCache_Click(object? sender, RoutedEventArgs e)
    {
        if (LinuxMaker.IdeRunning) { MakerIdeResult.Text = "Feche a Arduino IDE antes de limpar o cache."; return; }
        try { MakerIdeResult.Text = LinuxMaker.ClearIdeCache(); }
        catch (Exception ex) { MakerIdeResult.Text = $"Falhou: {ex.Message}"; }
    }
#endif
}
