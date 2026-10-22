using Vk = ABExplode.Vulkan;
using D3D = ABExplode.Platform.Windows;

// VkTest burn|d3d [segundos] [shells] [iterations] [msaa]
//   burn: stress de GPU em Vulkan (o do Linux); d3d: o mesmo teste em Direct3D 11 (o do Windows), para comparar.
//   game|d3dgame [segundos] [largura] [altura]: cena de jogo do RESET BENCH em Vulkan / Direct3D 11.
string mode = args.Length > 0 ? args[0] : "burn";
int I(int i, int def) => args.Length > i && int.TryParse(args[i], out int v) ? v : def;

if (mode is "game" or "d3dgame")
{
    var dur = TimeSpan.FromSeconds(I(1, 20));
    int w = I(2, 1920), h = I(3, 1080);
    string? err; bool cancelled;
    (double Avg, double Low1, int Frames, string Adapter)? res;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (mode == "game")
    {
        var gb = new Vk.GpuGameBench { Width = w, Height = h, Duration = dur };
        var r = gb.Run();
        res = r is null ? null : (r.AvgFps, r.Low1Fps, r.Frames, r.Adapter);
        err = gb.Error; cancelled = gb.Cancelled;
    }
    else
    {
        var gb = new D3D.GpuGameBench { Width = w, Height = h, Duration = dur };
        var r = gb.Run();
        res = r is null ? null : (r.AvgFps, r.Low1Fps, r.Frames, r.Adapter);
        err = gb.Error; cancelled = gb.Cancelled;
    }
    Console.WriteLine(res is { } x
        ? $"OK [{mode}] · {w}x{h} · média {x.Avg:0.0} FPS · 1% low {x.Low1:0.0} · {x.Frames} frames · nota {x.Avg * 10:0} · {x.Adapter} · {sw.Elapsed.TotalSeconds:0} s"
        : $"ERRO [{mode}]: {err ?? (cancelled ? "cancelado" : "?")}");
    return res is null ? 1 : 0;
}
int secs = I(1, 10), shells = I(2, 96), iterations = I(3, 64), msaa = I(4, 4);

Func<(bool Running, double Fps, long Frames, long Presents, double Avg, string? Error, string Adapter)> state;
Action stop;
if (mode == "burn")
{
    var b = new Vk.GpuBurn();
    b.Start(new Vk.GpuBurnSettings { Width = 1280, Height = 720, Shells = shells, Iterations = iterations, Msaa = msaa });
    state = () => (b.Running, b.Fps, b.Frames, b.Presents, b.AverageFps, b.Error, b.AdapterUsed);
    stop = b.Stop;
}
else if (mode == "d3d")
{
    var b = new D3D.GpuBurn();
    b.Start(new D3D.GpuBurnSettings { Width = 1280, Height = 720, Shells = shells, Iterations = iterations, Msaa = msaa });
    state = () => (b.Running, b.Fps, b.Frames, b.Presents, b.AverageFps, b.Error, b.AdapterUsed);
    stop = b.Stop;
}
else { Console.WriteLine("Modos: burn (Vulkan), d3d (Direct3D 11)"); return 2; }

var start = DateTime.Now;
while ((DateTime.Now - start).TotalSeconds < secs && (state().Running || (DateTime.Now - start).TotalSeconds < 2))
{
    Thread.Sleep(1000);
    var s = state();
    Console.WriteLine($"{(DateTime.Now - start).TotalSeconds:0}s · {s.Fps:0.0} FPS · {s.Frames} frames · {s.Presents} na tela · {s.Adapter}");
}
stop();
var f = state();
Console.WriteLine(f.Error != null ? $"ERRO: {f.Error}" : $"OK [{mode}] · média {f.Avg:0.0} FPS · {f.Frames} frames");
return f.Error != null ? 1 : 0;
