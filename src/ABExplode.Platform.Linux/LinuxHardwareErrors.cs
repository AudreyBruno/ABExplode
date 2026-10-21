using System.Globalization;
using System.Text.RegularExpressions;

namespace ABExplode.Platform.Linux;

/// <summary>
/// RESET OC: erros de hardware no log do kernel (journalctl -k) no lugar do WHEA do Windows. Conta Machine Check (MCE),
/// erros de PCIe (AER) e de memória (EDAC); e os travamentos da GPU (Xid da NVIDIA, reset do amdgpu, GPU HANG do i915).
/// </summary>
internal sealed class LinuxHardwareErrors : IHardwareErrors
{
    public bool IsSupported => Sys.HasCommand("journalctl");

    private static readonly Regex Hw = new(@"mce:|Machine check|Hardware Error|\[Hardware Error\]|EDAC .*(CE|UE)|PCIe Bus Error|AER: .*error", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Gpu = new(@"NVRM: Xid|amdgpu.*(GPU reset|ring .* timeout|GPU recovery)|i915.*GPU HANG|xe.*(GT reset|timed out)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public HardwareErrors ErrorsSince(DateTime since)
    {
        long epoch = new DateTimeOffset(since.ToUniversalTime()).ToUnixTimeSeconds();
        // Usuário comum só lê o journal se estiver no grupo systemd-journal/adm (o padrão na maioria das distros desktop).
        string? log = Sys.Run("journalctl", $"-k --since @{epoch} -o short-iso --no-pager -q", 15000);
        const string noAccess = "não foi possível ler o log do kernel (entre no grupo systemd-journal ou abra o app como root)";
        if (log == null)
            return new HardwareErrors(0, 0, null, Sys.HasCommand("journalctl") ? noAccess : "este sistema não usa o journal do systemd");
        // Sem permissão o journalctl não falha: só mostra o journal do próprio usuário, que não tem as mensagens do kernel.
        if (log.Trim().Length == 0 && !Sys.IsRoot &&
            Sys.Run("id", "-Gn")?.Split(' ', StringSplitOptions.TrimEntries) is { } groups &&
            !groups.Any(g => g is "systemd-journal" or "adm" or "wheel"))
            return new HardwareErrors(0, 0, null, noAccess);
        int hw = 0, gpu = 0;
        string? lastHw = null, lastGpu = null;
        foreach (var line in log.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (Hw.IsMatch(line)) { hw++; lastHw = Describe(line); }
            else if (Gpu.IsMatch(line)) { gpu++; lastGpu = Describe(line); }
        }
        return new HardwareErrors(hw, gpu, lastHw ?? lastGpu, null);
    }

    /// <summary>"2026-10-21T14:03:11-0300 host kernel: mce: [Hardware Error]: ..." → "21/10 14:03 · mce: [Hardware Error]: ...".</summary>
    private static string Describe(string line)
    {
        var f = line.Split(' ', 4);
        string when = f.Length > 0 && DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("dd/MM HH:mm") : "";
        string text = f.Length == 4 ? f[3] : line;
        if (text.StartsWith("kernel: ")) text = text[8..];
        if (text.Length > 160) text = text[..160] + "…";
        return when.Length > 0 ? $"{when} · {text}" : text;
    }

    public (string Maker, string Model) Motherboard() => (Sys.Dmi("board_vendor") ?? "", Sys.Dmi("board_name") ?? "");
}
