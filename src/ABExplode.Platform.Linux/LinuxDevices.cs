namespace ABExplode.Platform.Linux;

/// <summary>Dispositivo USB conectado (sem os hubs).</summary>
public sealed record LinuxUsbDevice(string Id, string Name, bool Storage);

/// <summary>
/// Leituras baratas para os avisos de "conectado/removido" (o app compara a cada poucos segundos):
/// pontos de montagem de disco e dispositivos USB de /sys/bus/usb/devices.
/// </summary>
public static class LinuxDevices
{
    /// <summary>Pontos de montagem de partições de disco (/dev/...), sem loop e sem /boot.</summary>
    public static HashSet<string> Mounts()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Sys.MountLines())
        {
            var f = line.Split(' ');
            if (f.Length < 3 || !f[0].StartsWith("/dev/") || f[0].StartsWith("/dev/loop")) continue;
            string mount = System.Text.RegularExpressions.Regex.Replace(f[1], @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
            if (mount.StartsWith("/boot") || mount.StartsWith("/snap") || mount == "/efi") continue;
            set.Add(mount);
        }
        return set;
    }

    public static List<LinuxUsbDevice> Usb()
    {
        var list = new List<LinuxUsbDevice>();
        foreach (var d in Sys.Dirs("/sys/bus/usb/devices"))
        {
            string id = Path.GetFileName(d);
            if (id.Contains(':') || id.StartsWith("usb")) continue; // interfaces e controladores raiz
            string? vid = Sys.Read(Path.Combine(d, "idVendor"));
            if (vid == null || Sys.Read(Path.Combine(d, "bDeviceClass")) == "09") continue; // hub
            string product = Sys.Read(Path.Combine(d, "product")) ?? $"{vid}:{Sys.Read(Path.Combine(d, "idProduct"))}";
            string? maker = Sys.Read(Path.Combine(d, "manufacturer"));
            string name = maker != null && !product.Contains(maker, StringComparison.OrdinalIgnoreCase) ? $"{product} · {maker}" : product;
            bool storage = Sys.Dirs(d, id + ":*").Any(i => Sys.Read(Path.Combine(i, "bInterfaceClass")) == "08");
            list.Add(new LinuxUsbDevice($"{id}/{vid}:{Sys.Read(Path.Combine(d, "idProduct"))}", name, storage));
        }
        return list;
    }
}
