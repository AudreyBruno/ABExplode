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
    private void InitDevices() { }
    private void CloseDevices() { }
#endif
}
