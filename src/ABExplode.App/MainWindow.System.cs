using System.Runtime.InteropServices;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia.Controls;
using Avalonia.Threading;
#if WINDOWS
using ABExplode.Platform.Windows;
#endif

namespace ABExplode.App;

/// <summary>Tela Sistema, cartões de unidade e avisos de dispositivo conectado/removido.</summary>
public partial class MainWindow
{
    // ---------------- Sistema ----------------

    private void InfoCategories_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (InfoCategories.SelectedItem is not InfoNode node) return;
        InfoTitle.Text = node.Header;
        InfoDetails.ItemsSource = node.Children;
        bool storage = node.Header == "Armazenamento";
        DrivesPanel.IsVisible = storage;
        if (storage) RefreshDrives();
    }

    // ---------------- Unidades ----------------

    private List<DriveCard> _drives = new();
    private ToastWindow? _toast;

    private async void RefreshDrives()
    {
        _drives = await Task.Run(_platform.SystemInfo.Drives);
        DriveCards.ItemsSource = _drives;
        long total = _drives.Sum(d => d.Total), free = _drives.Sum(d => d.Free);
        DrivesSummary.Text = $"{_drives.Count} unidades · {DriveCard.Fmt(free)} livres de {DriveCard.Fmt(total)}";
    }

    private void Toast(string title, string text, string icon)
    {
        AddEvent(icon, title, text.Split('\n')[0], "");
        _toast ??= new ToastWindow();
        _toast.Notify(title, text, icon);
    }

    /// <summary>Unidades montadas ou removidas: avisa e atualiza os cartões e a lista de discos físicos.</summary>
    private async void OnVolumes(List<string> letters, bool arrived)
    {
        if (arrived) await Task.Delay(800); // o sistema de arquivos leva um instante para montar
        var before = _drives;
        var now = await Task.Run(_platform.SystemInfo.Drives);
        foreach (var l in letters)
        {
            if (arrived && now.FirstOrDefault(d => d.Letter.Equals(l, StringComparison.OrdinalIgnoreCase)) is { } d)
                Toast(d.Removable ? "Pendrive conectado" : "Unidade conectada",
                      $"{d.Title} · {DriveCard.Fmt(d.Free)} livres de {DriveCard.Fmt(d.Total)}" + (d.Disk.Length > 0 ? $"\n{d.Disk}" : ""), "");
            else if (!arrived)
            {
                var old = before.FirstOrDefault(d => d.Letter.Equals(l, StringComparison.OrdinalIgnoreCase));
                Toast("Unidade removida", old != null ? $"{old.Title} pode ser desconectada." : $"{l} removida.", "");
            }
        }
        _drives = now;

        // Discos físicos (lista abaixo dos cartões) também mudam com pendrive/HD externo.
        if (InfoCategories.ItemsSource is IEnumerable<InfoNode> cats && cats.FirstOrDefault(c => c.Header == "Armazenamento") is { } storage)
        {
            await Task.Run(() => _platform.SystemInfo.RefreshDisks(storage));
            if (InfoCategories.SelectedItem == storage) { InfoDetails.ItemsSource = null; InfoDetails.ItemsSource = storage.Children; }
        }
        if (DrivesPanel.IsVisible) RefreshDrives();
    }

#if WINDOWS
    // ---------------- Dispositivos (Windows: WM_DEVICECHANGE) ----------------
    // TODO(Fase 4): IDeviceWatcher na plataforma, para o Linux (udev) usar o mesmo caminho.

    private Dictionary<string, UsbDevice> _usbPresent = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastVolumeEvent = DateTime.MinValue;
    private DispatcherTimer? _usbDebounce;
    private Win32Properties.CustomWndProcHookCallback? _wndProc;

    private void InitDevices()
    {
        SnapshotUsb();
        _wndProc = WndProc;
        Opened += (_, _) => Win32Properties.AddWndProcHookCallback(this, _wndProc);
    }

    private void CloseDevices()
    {
        if (_wndProc != null) Win32Properties.RemoveWndProcHookCallback(this, _wndProc);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE) OnDeviceChange((int)wParam, lParam);
        else if (msg == WM_HOTKEY && (int)wParam == HotkeyId) { ToggleOverlay(); handled = true; }
        return IntPtr.Zero;
    }

    private async void SnapshotUsb()
    {
        try
        {
            var list = await Task.Run(UsbCleaner.List);
            _usbPresent = list.Where(d => d.Present).GroupBy(d => d.InstanceId).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    /// <summary>WM_DEVICECHANGE: unidades (pendrive, HD externo) e qualquer dispositivo USB.</summary>
    private void OnDeviceChange(int ev, IntPtr lParam)
    {
        const int DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
        if ((ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero &&
            Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME)
        {
            int mask = Marshal.ReadInt32(lParam, 12); // DEV_BROADCAST_VOLUME.dbcv_unitmask: bit 0 = A:
            var letters = Enumerable.Range(0, 26).Where(i => (mask & (1 << i)) != 0).Select(i => $"{(char)('A' + i)}:").ToList();
            _lastVolumeEvent = DateTime.Now;
            OnVolumes(letters, ev == DBT_DEVICEARRIVAL);
        }
        else if (ev == DBT_DEVNODES_CHANGED)
        {
            // Chega várias vezes seguidas por dispositivo: espera assentar e compara com a lista anterior.
            if (_usbDebounce == null)
            {
                _usbDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _usbDebounce.Tick += (_, _) => { _usbDebounce!.Stop(); DiffUsb(); };
            }
            _usbDebounce.Stop();
            _usbDebounce.Start();
        }
    }

    private async void DiffUsb()
    {
        List<UsbDevice> list;
        try { list = await Task.Run(UsbCleaner.List); } catch { return; }
        var present = list.Where(d => d.Present).GroupBy(d => d.InstanceId)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = present.Values.Where(d => !_usbPresent.ContainsKey(d.InstanceId)).ToList();
        var removed = _usbPresent.Values.Where(d => !present.ContainsKey(d.InstanceId)).ToList();
        _usbPresent = present;
        if (PageMakerPorts.IsVisible) ScanUsb();
        if (PageMakerDrv.IsVisible) RefreshMakerDevices(); // placa plugada/removida: atualiza o diagnóstico

        // Pendrives já geram aviso de unidade: não repete com o nome genérico "armazenamento em massa".
        bool volumeJustNow = (DateTime.Now - _lastVolumeEvent).TotalSeconds < 4;
        static bool Storage(UsbDevice d) => d.Name.Contains("Mass Storage", StringComparison.OrdinalIgnoreCase) ||
                                            d.Name.Contains("Armazenamento", StringComparison.OrdinalIgnoreCase);
        // Um dispositivo composto (ex.: Arduino Leonardo) gera vários registros: anuncia o mais descritivo.
        static UsbDevice? Best(List<UsbDevice> l) =>
            l.FirstOrDefault(d => d.Port != null) ?? l.FirstOrDefault(d => d.Maker != null) ??
            l.FirstOrDefault(d => !d.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase) &&
                                  !d.Name.Contains("Hub", StringComparison.OrdinalIgnoreCase)) ?? l.FirstOrDefault();

        added = added.Where(d => !Storage(d)).ToList();
        removed = removed.Where(d => !Storage(d)).ToList();
        if (!volumeJustNow && Best(added) is { } a)
            Toast("Dispositivo USB conectado", a.Name + (a.Maker != null && !a.Name.Contains(a.Maker) ? $" · {a.Maker}" : ""), "");
        else if (!volumeJustNow && Best(removed) is { } r)
            Toast("Dispositivo USB desconectado", r.Name, "");
    }
#else
    // ---------------- Dispositivos (Linux: compara montagens e /sys/bus/usb a cada 2 s) ----------------
    // TODO(Fase 5): trocar a comparação periódica pelos eventos do udev (netlink).

    private DispatcherTimer? _devPoll;
    private HashSet<string> _mounts = new();
    private Dictionary<string, ABExplode.Platform.Linux.LinuxUsbDevice> _usbNow = new();

    private void InitDevices()
    {
        _mounts = ABExplode.Platform.Linux.LinuxDevices.Mounts();
        _usbNow = ABExplode.Platform.Linux.LinuxDevices.Usb().ToDictionary(d => d.Id);
        _devPoll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(2) };
        _devPoll.Tick += (_, _) => PollDevices();
        _devPoll.Start();
        Opened += async (_, _) => { await Task.Delay(1500); await CheckLinuxToolsAsync(); };
    }

    /// <summary>
    /// Na abertura: avisa do que falta para ler tudo (lspci, smartctl, módulo de sensores...) e instala pelo pkexec se o
    /// usuário aceitar. "Agora não" lembra na próxima abertura; sem como instalar pelo app, "Não lembrar mais" silencia
    /// esses itens. "ABExplode --deps" no terminal confere de novo.
    /// </summary>
    private async Task CheckLinuxToolsAsync()
    {
        var all = await Task.Run(ABExplode.Platform.Linux.LinuxDependencies.MissingTools);
        var missing = all.Where(d => !_settings.DepsDismissed.Contains(d.Key)).ToList();
        if (missing.Count == 0) return;

        var pm = ABExplode.Platform.Linux.LinuxDependencies.Detect();
        bool can = ABExplode.Platform.Linux.LinuxDependencies.CanInstall(pm);
        string how = ABExplode.Platform.Linux.LinuxDependencies.Instructions(missing, pm);
        string text = "Para o ABExplode ler tudo neste sistema, faltam:\n\n" +
                      string.Join("\n", missing.Select(d => $"• {d.Title}: {d.Why}")) + "\n\n" +
                      (can ? "O ABExplode pode instalar agora; o sistema vai pedir sua senha.\n\nOu instale pelo terminal:\n"
                           : "Instale pelo terminal:\n") + how +
                      (pm?.Immutable == true ? "\n\nEsta distribuição é imutável: depois de instalar, reinicie o computador." : "") +
                      "\n\nPara conferir de novo depois: ABExplode --deps";

        void Dismiss()
        {
            // não pergunta mais por estes itens na abertura (os que surgirem depois ainda são avisados)
            _settings.DepsDismissed.AddRange(missing.Select(d => d.Key).Except(_settings.DepsDismissed));
            try { _settings.Save(); } catch { }
        }
        if (!can)
        {
            // Sem como instalar pelo app (sem pkexec ou distro imutável): só mostra os comandos; OK lembra na próxima abertura.
            if (!await Dialog.Ask(this, "Dependências", text, "OK", "NÃO LEMBRAR MAIS")) Dismiss();
            return;
        }
        if (!await Dialog.Ask(this, "Dependências", text, "INSTALAR", "AGORA NÃO")) return; // lembra na próxima abertura

        Toast("Instalando dependências", string.Join(", ", missing.Select(d => d.Title)), "");
        var (ok, output) = await ABExplode.Platform.Linux.LinuxDependencies.InstallAsync(missing, pm!);
        if (ok)
            await Dialog.Show(this, "Dependências", "Instalado. Feche e abra o ABExplode para ele usar as ferramentas novas " +
                                                    "(o sensor da CPU já começa a aparecer).");
        else
            await Dialog.Show(this, "Dependências", $"A instalação não terminou:\n\n{output}\n\nTente pelo terminal:\n{how}");
    }

    private void CloseDevices() => _devPoll?.Stop();

    private void PollDevices()
    {
        var mounts = ABExplode.Platform.Linux.LinuxDevices.Mounts();
        var added = mounts.Except(_mounts).ToList();
        var removed = _mounts.Except(mounts).ToList();
        _mounts = mounts;
        if (added.Count > 0) OnVolumes(added, true);
        if (removed.Count > 0) OnVolumes(removed, false);

        var usb = ABExplode.Platform.Linux.LinuxDevices.Usb().ToDictionary(d => d.Id);
        // Pendrives e HDs externos já geram aviso de unidade.
        var newUsb = usb.Values.Where(d => !_usbNow.ContainsKey(d.Id) && !d.Storage).ToList();
        var goneUsb = _usbNow.Values.Where(d => !usb.ContainsKey(d.Id) && !d.Storage).ToList();
        _usbNow = usb;
        if (newUsb.FirstOrDefault() is { } a) Toast("Dispositivo USB conectado", a.Name, "");
        else if (goneUsb.FirstOrDefault() is { } r) Toast("Dispositivo USB desconectado", r.Name, "");
    }
#endif
}
