using System.Text;
using ABExplode.Core;
using ABExplode.Platform;

namespace ABExplode.App;

/// <summary>
/// "ABExplode --diag": imprime tudo o que a plataforma leu, sem abrir janela. Serve para conferir um sistema novo
/// (principalmente o Linux) e mandar a saída quando alguma leitura vier errada.
/// </summary>
internal static class Diagnostics
{
    public static int Run(IPlatform p)
    {
        var o = new StringBuilder();
        void Section(string title) => o.Append("\n===== ").Append(title).Append(" =====\n");
        void Try(string title, Action a)
        {
            Section(title);
            try { a(); }
            catch (Exception ex) { o.Append("ERRO: ").Append(ex).Append('\n'); }
        }
        void Tree(InfoNode n, int depth)
        {
            o.Append(new string(' ', depth * 2)).Append(n.Display).Append('\n');
            foreach (var c in n.Children) Tree(c, depth + 1);
        }

        o.Append($"ABExplode {AppVersion.Full} · {p.Name} · {(p.IsElevated ? "administrador/root" : "usuário comum")} · {Environment.OSVersion}\n");
        Try("Sistema", () => { foreach (var n in p.SystemInfo.Collect()) Tree(n, 0); });
        Try("Memória", () =>
        {
            var (total, avail) = p.SystemInfo.MemoryStatus();
            o.Append($"total {total >> 20} MB · disponível {avail >> 20} MB · CPU: {p.SystemInfo.CpuName()}\n");
        });
        Try("Placas de vídeo", () =>
        {
            foreach (var g in p.Gpu.Collect())
            {
                o.Append($"{g.Name} [{g.Vendor}] · {g.Subtitle} · VRAM {g.VramBytes >> 20} MB · NVML {(g.NvmlHandle != IntPtr.Zero ? "sim" : "não")}\n");
                foreach (var s in g.Sections) Tree(s, 1);
                foreach (var kv in g.LiveExtras()) o.Append($"  {kv.Key}: {kv.Value}\n");
            }
        });

        List<SensorRow> rows = new();
        Try("Sensores (2 leituras com 1 s entre elas)", () =>
        {
            using var s = p.CreateSensors();
            s.OpenAsync().Wait();
            s.Refresh();
            Thread.Sleep(1000);
            s.Refresh();
            rows = s.Rows.ToList();
            foreach (var r in rows.OrderBy(r => r.HardwareType).ThenBy(r => r.Hardware).ThenBy(r => r.Order).ThenBy(r => r.Name))
                o.Append($"[{r.HardwareType}] {r.Hardware} · {r.Category} · {r.Name} = {r.Value}\n");
            o.Append($"CPU: temperatura {s.CpuTemperature?.ToString("0.#") ?? "—"} °C · carga {s.CpuLoad?.ToString("0") ?? "—"} % · consumo {s.CpuPower?.ToString("0.#") ?? "—"} W\n");
            var problem = s.DiagnoseCpuSensors();
            if (problem != CpuSensorProblem.None) o.Append($"Diagnóstico: {problem} · {s.Explain(problem)}\n");
        });
        Try("Unidades", () =>
        {
            foreach (var d in p.SystemInfo.Drives())
                o.Append($"{d.Title} · {d.Meta} · {d.Sizes} · removível {d.Removable}\n");
        });
        Try("Unidades para o teste de disco", () =>
        {
            foreach (var (root, text) in DiskBench.Drives()) o.Append($"{text} → {root}\n");
        });
        Try("Saúde dos discos", () =>
        {
            foreach (var c in p.DiskHealth.Read(rows))
            {
                o.Append($"{c.Title} · {c.Meta} · {c.VerdictText}\n  {c.Reasons}\n");
                foreach (var kv in c.Metrics) o.Append($"  {kv.Key}: {kv.Value}\n");
            }
        });
        Try("Bateria e energia", () =>
        {
            foreach (var c in p.SystemInfo.Battery(rows))
            {
                o.Append($"{c.Title} · {c.Meta} · {c.VerdictText}\n  {c.Reasons}\n");
                foreach (var kv in c.Metrics) o.Append($"  {kv.Key}: {kv.Value}\n");
            }
        });
        Try("Erros de hardware (últimas 24 h)", () =>
        {
            var e = p.HardwareErrors.ErrorsSince(DateTime.Now.AddDays(-1));
            o.Append($"hardware {e.Whea} · GPU {e.GpuResets} · último: {e.LastDetail ?? "—"} · erro: {e.Error ?? "—"}\n");
            var (maker, model) = p.HardwareErrors.Motherboard();
            o.Append($"placa-mãe: {maker} {model}\n");
        });
        Try("Jogos", () =>
        {
            var games = p.Games.Scan(Array.Empty<CustomGame>());
            o.Append($"{games.Count} jogos\n");
            foreach (var g in games) o.Append($"{g.Store} · {g.Name} · {g.InstallDir}\n");
        });
        Try("FPS", () =>
        {
            using var f = p.CreateFrameSource();
            o.Append($"suportado {f.IsSupported} · {f.Error ?? "ok"}\n");
        });

        // Bytes UTF-8 direto na saída: o console do Windows usaria a página de código antiga e estragaria os acentos.
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(o.ToString()));
        return 0;
    }
}
