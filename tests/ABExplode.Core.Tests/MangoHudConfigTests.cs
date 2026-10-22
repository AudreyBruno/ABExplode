using ABExplode.Platform.Linux;

namespace ABExplode.Core.Tests;

public class MangoHudConfigTests
{
    private static string[] Lines(IReadOnlySet<string> show, string corner = "TL", double scale = 1) =>
        LinuxMangoHud.BuildConfig(show, corner, scale).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Padrao_do_overlay_vira_as_opcoes_do_MangoHud()
    {
        var l = Lines(OverlayMetrics.Defaults());
        Assert.StartsWith("# Gerado pelo ABExplode", l[0]);
        foreach (var o in new[] { "fps", "frametime", "frame_timing", "gpu_stats", "gpu_temp", "gpu_core_clock", "gpu_power", "vram",
                                  "cpu_stats", "cpu_temp", "cpu_mhz", "cpu_power", "ram", "exec_name", "fps_metrics=avg,0.01" })
            Assert.Contains(o, l);
        Assert.Contains("toggle_hud=Control_L+Shift_L+o", l);
    }

    [Fact]
    public void O_que_o_MangoHud_mostra_sozinho_vai_desligado_quando_desmarcado()
    {
        var l = Lines(new HashSet<string> { "fps" });
        Assert.Contains("fps", l);
        Assert.Contains("frametime=0", l);
        Assert.Contains("frame_timing=0", l);
        Assert.Contains("gpu_stats=0", l);
        Assert.Contains("cpu_stats=0", l);
        Assert.DoesNotContain("gpu_temp", l); // o que já vem desligado não precisa de =0
    }

    [Theory]
    [InlineData("TL", "position=top-left")]
    [InlineData("TR", "position=top-right")]
    [InlineData("BL", "position=bottom-left")]
    [InlineData("BR", "position=bottom-right")]
    public void Canto_vira_a_posicao(string corner, string expected) => Assert.Contains(expected, Lines(new HashSet<string>(), corner));

    [Fact]
    public void Tamanho_vira_font_scale_com_ponto() => Assert.Contains("font_scale=1.3", Lines(new HashSet<string>(), "TL", 1.3));
}
