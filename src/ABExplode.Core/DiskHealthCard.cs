namespace ABExplode.Core;

public enum DiskVerdict { Good, Warning, Critical, Unknown }

/// <summary>Cartão de saúde de um disco físico (já com os textos prontos para a tela e para o relatório).</summary>
public sealed class DiskHealthCard
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Serial { get; init; } = "";
    public DiskVerdict Verdict { get; init; } = DiskVerdict.Unknown;
    /// <summary>Texto do selo; quando vazio, usa o padrão de saúde de disco. (Também usado por Energia e Rede.)</summary>
    public string? Label { get; init; }
    public string VerdictText => Label ?? Verdict switch
    {
        DiskVerdict.Good => "SAUDÁVEL",
        DiskVerdict.Warning => "ATENÇÃO",
        DiskVerdict.Critical => "CRÍTICO",
        _ => "SEM DADOS"
    };
    /// <summary>Atenção, crítico e "sem dados" ganham destaque (selo preenchido); saudável fica só com contorno.</summary>
    public bool IsAlert => Verdict != DiskVerdict.Good;
    public string Reasons { get; init; } = "";
    public List<KeyValuePair<string, string>> Metrics { get; init; } = new();
}
