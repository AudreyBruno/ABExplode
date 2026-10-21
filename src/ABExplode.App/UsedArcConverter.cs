using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ABExplode.App;

/// <summary>Fração usada (0–1) → arco do anel do cartão de unidade, num quadrado de 84×84 com traço centrado no raio 36.</summary>
public sealed class UsedArcConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double frac ? Arc(frac, 42, 42, 36) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Geometry? Arc(double frac, double cx, double cy, double r)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        if (frac <= 0) return null;
        double a = frac * 2 * Math.PI;
        var start = new Point(cx, cy - r);                                       // topo, sentido horário
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return g;
    }
}
