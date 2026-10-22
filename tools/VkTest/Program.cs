using Vk = ABExplode.Vulkan;
using D3D = ABExplode.Platform.Windows;

// VkTest burn|d3d [segundos] [shells] [iterations] [msaa]
//   burn: stress de GPU em Vulkan (o do Linux); d3d: o mesmo teste em Direct3D 11 (o do Windows), para comparar.
string mode = args.Length > 0 ? args[0] : "burn";
int I(int i, int def) => args.Length > i && int.TryParse(args[i], out int v) ? v : def;
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
