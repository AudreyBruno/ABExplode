namespace ABExplode.Platform;

/// <summary>Cartão de uma unidade (letra). O anel de ocupação é desenhado pela UI a partir de <see cref="UsedFraction"/>.</summary>
public sealed class DriveCard
{
    public string Letter { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public string Disk { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public bool Removable { get; init; }

    public double UsedFraction => Total > 0 ? (double)(Total - Free) / Total : 0;
    public string UsedPercent => $"{UsedFraction:P0}";
    public string Title => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter}  {Label}";
    public string Sizes => $"{Fmt(Total - Free)} usados · {Fmt(Free)} livres de {Fmt(Total)}";
    public string Meta => string.Join(" · ", new[] { Kind, FileSystem, Disk }.Where(s => !string.IsNullOrEmpty(s)));

    public static string Fmt(long b)
    {
        double v = b; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
