using System.Text.RegularExpressions;

namespace ABExplode.Platform;

/// <summary>Argumentos de inicialização do jogo a partir do perfil (igual nos dois sistemas; o Proton repassa os mesmos ao jogo).</summary>
public static class GameArgs
{
    /// <summary>Monta a linha de argumentos do perfil (modo de janela, resolução, taxa e extras) no estilo da engine escolhida.</summary>
    public static string Build(GameProfile p)
    {
        var a = new List<string>();
        int w = 0, h = 0;
        var m = Regex.Match(p.Res ?? "", @"^(\d+)x(\d+)$");
        if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }

        switch (p.Style)
        {
            case "unity":
                if (p.WinMode == "full") a.Add("-screen-fullscreen 1");
                else if (p.WinMode == "window") a.Add("-screen-fullscreen 0");
                else if (p.WinMode == "borderless") a.Add("-popupwindow");
                if (w > 0) a.Add($"-screen-width {w} -screen-height {h}");
                break;
            case "unreal":
                if (p.WinMode == "full") a.Add("-fullscreen");
                else if (p.WinMode is "window" or "borderless") a.Add("-windowed");
                if (w > 0) a.Add($"-ResX={w} -ResY={h}");
                break;
            default: // source / CS2
                if (p.WinMode == "full") a.Add("-fullscreen");
                else if (p.WinMode == "window") a.Add("-windowed");
                else if (p.WinMode == "borderless") a.Add("-windowed -noborder");
                if (w > 0) a.Add($"-w {w} -h {h}");
                if (p.Hz > 0) a.Add($"-freq {p.Hz}");
                break;
        }
        if (!string.IsNullOrWhiteSpace(p.Extra)) a.Add(p.Extra.Trim());
        return string.Join(" ", a);
    }
}
