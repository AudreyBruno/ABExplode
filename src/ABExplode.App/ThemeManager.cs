using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ABExplode.App;

/// <summary>
/// Troca entre modo escuro e claro substituindo os pincéis da paleta (Palette.axaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// </summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }

    private static readonly (string Key, string Dark, string Light)[] Palette =
    {
        ("Bg", "#000000", "#FFFFFF"),
        ("Panel", "#080808", "#F2F2F2"),
        ("Card", "#0E0E0E", "#FFFFFF"),
        ("Hover", "#181818", "#E4E4E4"),
        ("Line", "#1F1F1F", "#CFCFCF"),
        ("LineStrong", "#3A3A3A", "#8A8A8A"),
        ("Fg", "#F5F5F5", "#111111"),
        ("Muted", "#8C8C8C", "#3F3F3F"),
        ("Faint", "#4A4A4A", "#5C5C5C"),
        ("Ink", "#000000", "#FFFFFF"),
        ("ThumbBrush", "#2E2E2E", "#CFCFCF"),
        ("ThumbHover", "#6A6A6A", "#A5A5A5"),
        ("ThumbDrag", "#9A9A9A", "#7A7A7A"),
        // Slider do tema Fluent: trilha = Line, parte preenchida e botão = Fg
        ("SliderTrackFill", "#1F1F1F", "#CFCFCF"),
        ("SliderTrackFillPointerOver", "#1F1F1F", "#CFCFCF"),
        ("SliderTrackFillPressed", "#1F1F1F", "#CFCFCF"),
        ("SliderTrackValueFill", "#F5F5F5", "#111111"),
        ("SliderTrackValueFillPointerOver", "#F5F5F5", "#111111"),
        ("SliderTrackValueFillPressed", "#F5F5F5", "#111111"),
        ("SliderThumbBackground", "#F5F5F5", "#111111"),
        ("SliderThumbBackgroundPointerOver", "#F5F5F5", "#111111"),
        ("SliderThumbBackgroundPressed", "#F5F5F5", "#111111"),
    };

    public static void Apply(bool light)
    {
        IsLight = light;
        var app = Application.Current!;
        foreach (var (key, dark, lightHex) in Palette)
            app.Resources[key] = new SolidColorBrush(Color.Parse(light ? lightHex : dark));
        // Controles do Fluent que não têm tema próprio aqui (barras de rolagem, menus) seguem o modo.
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}
