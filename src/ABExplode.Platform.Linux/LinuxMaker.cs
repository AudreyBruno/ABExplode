using System.Runtime.InteropServices;

namespace ABExplode.Platform.Linux;

/// <summary>Família de chip USB-serial: como reconhecer pelo VID, o módulo do kernel e onde está a documentação oficial.</summary>
public sealed record LinuxSerialChip(string Name, string[] Vids, string Module, string OfficialUrl);

/// <summary>Placa ou conversor serial conectado, com o diagnóstico. Mesmas propriedades do modelo do Windows.</summary>
public sealed class LinuxSerialDevice
{
    public string Name { get; init; } = "";
    public string Port { get; init; } = "";            // /dev/ttyUSB0 (vazio se nenhum driver criou a porta)
    public string Vid { get; init; } = "";
    public string Pid { get; init; } = "";
    public LinuxSerialChip? Chip { get; init; }
    public string Driver { get; init; } = "";          // módulo que pegou a placa (ch341, cdc_acm, brltty...)
    public string Verdict { get; init; } = "";
    public string Advice { get; init; } = "";
    public bool Ok { get; init; }
    internal string SysPath { get; init; } = "";       // /sys/bus/usb/devices/1-2
    public string Detail => string.Join(" · ", new[] { Port, Chip?.Name, $"VID {Vid} PID {Pid}" }.Where(s => !string.IsNullOrEmpty(s)));
    public string State => Ok ? "FUNCIONANDO" : "COM PROBLEMA";
}

/// <summary>Situação de permissão e conflitos para usar portas seriais neste usuário.</summary>
public sealed record SerialAccess(string Group, bool InGroup, bool AddedButNeedsRelogin, bool BrlttyActive, bool BrlttyMasked);

/// <summary>
/// RESET MAKER no Linux. Não há driver para instalar (CH340, CP210x, FTDI, PL2303 e CDC-ACM vêm no kernel); os problemas
/// de verdade são permissão (grupo dialout/uucp), o brltty pegando o CH340 (Ubuntu) e programas segurando a porta.
/// </summary>
public static class LinuxMaker
{
    public static readonly LinuxSerialChip[] Chips =
    {
        new("CH340/CH341", new[] { "1A86" }, "ch341", "https://www.wch-ic.com/downloads/CH341SER_LINUX_ZIP.html"),
        new("CP210x", new[] { "10C4" }, "cp210x", "https://www.silabs.com/developer-tools/usb-to-uart-bridge-vcp-drivers"),
        new("FTDI", new[] { "0403" }, "ftdi_sio", "https://ftdichip.com/drivers/vcp-drivers/"),
        new("Prolific PL2303", new[] { "067B" }, "pl2303", "https://www.prolific.com.tw/"),
        new("Arduino", new[] { "2341", "2A03" }, "cdc_acm", "https://docs.arduino.cc/software/ide-v2/tutorials/getting-started/ide-v2-downloading-and-installing/"),
        new("Espressif (ESP32-S2/S3/C3)", new[] { "303A" }, "cdc_acm", "https://docs.espressif.com/projects/esp-idf/en/latest/esp32/get-started/establish-serial-connection.html"),
        new("Raspberry Pi Pico", new[] { "2E8A" }, "cdc_acm", "https://www.raspberrypi.com/documentation/microcontrollers/"),
    };

    public static LinuxSerialChip? ChipByVid(string vid) => Chips.FirstOrDefault(c => c.Vids.Contains(vid, StringComparer.OrdinalIgnoreCase));

    [DllImport("libc", SetLastError = true)] private static extern int access(string path, int mode);
    private static bool CanOpen(string dev) { try { return access(dev, 6) == 0; } catch { return false; } } // R_OK | W_OK

    // ------------------------------------------------------------------ portas e placas

    /// <summary>Portas seriais USB existentes (/dev/ttyUSB*, /dev/ttyACM*) → caminho do dispositivo USB no /sys.</summary>
    private static Dictionary<string, string> Ttys()
    {
        var map = new Dictionary<string, string>();
        foreach (var t in Sys.Dirs("/sys/class/tty").Where(t => Path.GetFileName(t).StartsWith("ttyUSB") || Path.GetFileName(t).StartsWith("ttyACM")))
        {
            // device → interface USB (1-2:1.0); a pasta de cima é o dispositivo (1-2)
            string iface = Sys.RealPath(Path.Combine(t, "device"));
            string? usb = Path.GetFileName(iface).Contains(':') ? Path.GetDirectoryName(iface) : Path.GetDirectoryName(Path.GetDirectoryName(iface));
            if (usb != null) map["/dev/" + Path.GetFileName(t)] = usb;
        }
        return map;
    }

    /// <summary>Placas e conversores seriais conectados (pelo VID), com ou sem porta criada, e o diagnóstico de cada um.</summary>
    public static List<LinuxSerialDevice> ConnectedDevices()
    {
        var ttys = Ttys();
        var acc = Access();
        var list = new List<LinuxSerialDevice>();
        foreach (var d in Sys.Dirs("/sys/bus/usb/devices"))
        {
            string id = Path.GetFileName(d);
            if (id.Contains(':') || id.StartsWith("usb")) continue;
            string vid = (Sys.Read(Path.Combine(d, "idVendor")) ?? "").ToUpperInvariant();
            string pid = (Sys.Read(Path.Combine(d, "idProduct")) ?? "").ToUpperInvariant();
            string real = Sys.RealPath(d);
            var tty = ttys.FirstOrDefault(kv => kv.Value == real);
            var chip = ChipByVid(vid);
            if (chip == null && tty.Key == null) continue; // não é placa nem conversor serial

            string product = Sys.Read(Path.Combine(d, "product")) ?? chip?.Name ?? $"{vid}:{pid}";
            string? maker = Sys.Read(Path.Combine(d, "manufacturer"));
            string name = maker != null && !product.Contains(maker, StringComparison.OrdinalIgnoreCase) ? $"{product} · {maker}" : product;
            // Módulo que pegou cada interface (a primeira com driver)
            string driver = Sys.Dirs(d, id + ":*").Select(i => Sys.LinkName(Path.Combine(i, "driver"))).FirstOrDefault(x => x != null) ?? "";

            string verdict, advice;
            bool ok = false;
            if (tty.Key != null && CanOpen(tty.Key))
            {
                ok = true;
                verdict = $"Funcionando em {tty.Key}.";
                advice = "Na Arduino IDE, escolha essa porta em Ferramentas › Porta.";
            }
            else if (tty.Key != null)
            {
                verdict = $"A porta {tty.Key} existe, mas este usuário não tem permissão para abri-la.";
                advice = acc.AddedButNeedsRelogin
                    ? $"Você já está no grupo {acc.Group}: saia e entre de novo na sessão (ou reinicie) para valer."
                    : $"Entre no grupo {acc.Group} com o botão na tela Portas COM e depois saia e entre na sessão.";
            }
            else if (driver == "usbfs" && acc.BrlttyActive || driver.Contains("brltty"))
            {
                verdict = "O brltty (leitor de tela em braille) pegou esta placa e a porta não foi criada.";
                advice = "Problema clássico do CH340 no Ubuntu. Desative o brltty na tela Portas COM e plugue a placa de novo.";
            }
            else if (driver.Length == 0)
            {
                verdict = "Nenhum driver pegou esta placa.";
                advice = chip != null
                    ? $"O módulo {chip.Module} pode não estar carregado. Teste: sudo modprobe {chip.Module} e plugue de novo. Se for só um cabo de carga, troque o cabo."
                    : "Confira o cabo (muitos são só de carga) e plugue de novo.";
            }
            else
            {
                verdict = $"A placa usa o driver {driver}, mas nenhuma porta serial foi criada.";
                advice = "Pode ser um modo diferente (bootloader, armazenamento). Aperte o reset da placa ou plugue de novo.";
            }
            list.Add(new LinuxSerialDevice
            {
                Name = name, Port = tty.Key ?? "", Vid = vid, Pid = pid, Chip = chip, Driver = driver.Length > 0 ? driver : "nenhum",
                Verdict = verdict, Advice = advice, Ok = ok, SysPath = d,
            });
        }
        return list.OrderBy(x => x.Port.Length == 0).ThenBy(x => x.Port).ToList();
    }

    /// <summary>Desconecta e reconecta a placa pelo /sys (authorized 0 → 1): o kernel detecta de novo e reescolhe o driver.</summary>
    public static async Task<string> ReconnectAsync(LinuxSerialDevice d)
    {
        string f = Path.Combine(d.SysPath, "authorized");
        var (ok, output) = await Sys.RunAsRootAsync($"echo 0 > {f} && sleep 1 && echo 1 > {f}");
        return ok ? $"{d.Name} foi reconectada." : "Não foi possível reconectar: " + output;
    }

    // ------------------------------------------------------------------ permissão e brltty

    /// <summary>Grupo dono das portas seriais: dialout (Debian, Ubuntu, Fedora) ou uucp (Arch, openSUSE).</summary>
    private static string SerialGroup()
    {
        var groups = Sys.Lines("/etc/group").Select(l => l.Split(':')[0]).ToHashSet();
        if (Sys.InFlatpak) groups = (Sys.Run("getent", "group") ?? "").Split('\n').Select(l => l.Split(':')[0]).ToHashSet();
        return groups.Contains("dialout") ? "dialout" : groups.Contains("uucp") ? "uucp" : "dialout";
    }

    public static SerialAccess Access()
    {
        string group = SerialGroup();
        string user = Environment.UserName;
        // Grupos da sessão atual (id -Gn) e os gravados no sistema (id -Gn usuário lê o /etc/group de novo)
        var session = (Sys.Run("id", "-Gn") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var saved = (Sys.Run("id", $"-Gn {user}") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool inSession = session.Contains(group), inSaved = saved.Contains(group);

        string brltty = Sys.Run("systemctl", "is-enabled brltty-udev.service", 5000, acceptNonZero: true)?.Trim() ?? "";
        bool masked = brltty == "masked";
        bool active = Sys.HasCommand("brltty") && !masked && brltty is "enabled" or "static" or "indirect" or "alias";
        return new SerialAccess(group, inSession, !inSession && inSaved, active, masked);
    }

    /// <summary>Coloca o usuário no grupo das portas seriais (pede senha). Vale depois de sair e entrar na sessão.</summary>
    public static async Task<string> JoinGroupAsync()
    {
        string group = SerialGroup();
        var (ok, output) = await Sys.RunAsRootAsync($"usermod -aG {group} {Environment.UserName}");
        return ok ? $"Pronto: seu usuário entrou no grupo {group}. Saia e entre de novo na sessão (ou reinicie) para valer."
                  : "Não foi possível: " + output;
    }

    /// <summary>
    /// Desativa (ou reativa) o brltty, que no Ubuntu pega o CH340 achando que é uma linha braille. Usa systemctl mask, que
    /// é reversível e não desinstala nada (quem usa leitor braille deve deixar ligado).
    /// </summary>
    public static async Task<string> SetBrlttyAsync(bool disable)
    {
        string script = disable
            ? "systemctl mask brltty-udev.service brltty.service && systemctl stop brltty-udev.service brltty.service; true"
            : "systemctl unmask brltty-udev.service brltty.service";
        var (ok, output) = await Sys.RunAsRootAsync(script);
        if (!ok) return "Não foi possível: " + output;
        return disable ? "brltty desativado. Desconecte e plugue a placa de novo." : "brltty reativado.";
    }

    // ------------------------------------------------------------------ módulos do kernel

    /// <summary>Situação de cada módulo de chip serial: carregado, disponível ou ausente neste kernel.</summary>
    public static List<string> ModuleStatus()
    {
        string kernel = Sys.Read("/proc/sys/kernel/osrelease") ?? "";
        var lines = new List<string>();
        foreach (var m in Chips.Select(c => (c.Module, c.Name)).GroupBy(x => x.Module))
        {
            string names = string.Join(", ", m.Select(x => x.Name));
            string state = Directory.Exists($"/sys/module/{m.Key}") ? "carregado"
                         : Sys.Run("modinfo", $"-n {m.Key}") != null ? "disponível (carrega ao plugar)"
                         : "não encontrado neste kernel";
            lines.Add($"• {m.Key} ({names}): {state}");
        }
        lines.Add($"Kernel {kernel}. Esses drivers já vêm no kernel: não é preciso instalar nada.");
        return lines;
    }

    // ------------------------------------------------------------------ Arduino IDE

    public const string IdeFlatpak = "cc.arduino.IDE2";

    /// <summary>Versão da Arduino IDE instalada (Flatpak) ou "instalada" se vier da distribuição; null se não houver.</summary>
    public static string? IdeVersion()
    {
        if (Sys.Run("flatpak", $"info {IdeFlatpak}", 10000) is string info &&
            info.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Version:")) is string v)
            return v.Split(':', 2)[1].Trim() + " (Flatpak)";
        if (Sys.HasCommand("arduino-ide") || Sys.HasCommand("arduino")) return "da distribuição";
        return null;
    }

    public static readonly string[] PortHolders =
        { "serial-monitor", "serial-discovery", "arduino-cli", "minicom", "picocom", "screen", "putty", "cutecom", "moserial", "CoolTerm",
          "cura", "UltiMaker-Cura", "PrusaSlicer", "prusa-slicer", "pronterface", "Repetier-Host" };

    /// <summary>Processos com uma porta serial aberta (pid, nome), pelos descritores em /proc; no Flatpak, pelo fuser do sistema.</summary>
    public static List<(int Pid, string Name)> PortHolderProcesses()
    {
        var ports = Ttys().Keys.ToList();
        var result = new List<(int, string)>();
        if (ports.Count == 0) return result;
        if (Sys.InFlatpak)
        {
            // fuser escreve os PIDs na saída padrão e o resto na de erro
            foreach (var tok in (Sys.Run("fuser", string.Join(' ', ports), 5000, acceptNonZero: true) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(tok.TrimEnd('c', 'e', 'f', 'm', 'r'), out int pid))
                    result.Add((pid, (Sys.Run("ps", $"-p {pid} -o comm=") ?? "").Trim()));
            return result;
        }
        foreach (var d in Sys.Dirs("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(d), out int pid)) continue;
            string[] fds;
            try { fds = Directory.GetFiles(Path.Combine(d, "fd")); } catch { continue; }
            foreach (var fd in fds)
            {
                string? target = null;
                try { target = new FileInfo(fd).LinkTarget; } catch { }
                if (target != null && ports.Contains(target)) { result.Add((pid, Sys.Read(Path.Combine(d, "comm")) ?? "")); break; }
            }
        }
        return result;
    }

    public static bool IdeRunning => PortHolderProcesses().Any(p => p.Name.Contains("arduino", StringComparison.OrdinalIgnoreCase) && !p.Name.StartsWith("serial"))
                                     || (Sys.Run("pgrep", "-f arduino-ide") ?? "").Trim().Length > 0;

    /// <summary>Encerra os programas que seguram a porta (SIGTERM).</summary>
    public static int CloseHolders(IEnumerable<int> pids)
    {
        int n = 0;
        foreach (int pid in pids)
            if (Sys.Run("kill", $"-TERM {pid}") != null) n++;
        return n;
    }

    /// <summary>Apaga os índices de placas e os downloads temporários da Arduino IDE (as placas e bibliotecas instaladas ficam).</summary>
    public static string ClearIdeCache()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Path.Combine(home, ".arduino15"), Path.Combine(home, ".var/app", IdeFlatpak, ".arduino15") }.Where(Directory.Exists).ToList();
        if (roots.Count == 0) return "Pasta .arduino15 não encontrada (a IDE ainda não foi aberta neste usuário).";
        int n = 0;
        long freed = 0;
        foreach (var root in roots)
        {
            foreach (var f in Directory.GetFiles(root, "*_index*.json*"))
                try { File.Delete(f); n++; } catch { }
            string staging = Path.Combine(root, "staging");
            if (!Directory.Exists(staging)) continue;
            try { freed += new DirectoryInfo(staging).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            try { Directory.Delete(staging, true); } catch { }
        }
        return $"{n} índices apagados e {freed / 1048576.0:0} MB de downloads temporários liberados. A IDE baixa os índices de novo ao abrir.";
    }
}
