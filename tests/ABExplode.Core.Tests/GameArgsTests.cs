using ABExplode.Platform;

namespace ABExplode.Core.Tests;

public class GameArgsTests
{
    [Fact]
    public void Perfil_padrao_nao_gera_argumentos() => Assert.Equal("", GameArgs.Build(new GameProfile()));

    [Fact]
    public void Source_tela_cheia_resolucao_e_hz()
    {
        var p = new GameProfile { Style = "source", WinMode = "full", Res = "1920x1080", Hz = 144, Extra = "-novid" };
        Assert.Equal("-fullscreen -w 1920 -h 1080 -freq 144 -novid", GameArgs.Build(p));
    }

    [Fact]
    public void Source_sem_bordas() =>
        Assert.Equal("-windowed -noborder", GameArgs.Build(new GameProfile { Style = "source", WinMode = "borderless" }));

    [Fact]
    public void Unity_usa_os_argumentos_da_engine()
    {
        var p = new GameProfile { Style = "unity", WinMode = "borderless", Res = "2560x1440", Hz = 165 };
        Assert.Equal("-popupwindow -screen-width 2560 -screen-height 1440", GameArgs.Build(p)); // Unity não tem argumento de Hz
    }

    [Fact]
    public void Unreal_janela_e_resolucao()
    {
        var p = new GameProfile { Style = "unreal", WinMode = "window", Res = "1280x720" };
        Assert.Equal("-windowed -ResX=1280 -ResY=720", GameArgs.Build(p));
    }
}
