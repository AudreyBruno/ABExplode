using System.Reflection;

namespace ABExplode.App;

/// <summary>
/// Versão do app. As builds de desenvolvimento (branches fora da main, no build.yml) recebem
/// InformationalVersion "dev-&lt;branch&gt;-&lt;commit&gt;", e aí a tela mostra isso no lugar da versão normal.
/// </summary>
internal static class AppVersion
{
    private static readonly Assembly Asm = typeof(AppVersion).Assembly;

    /// <summary>"0.16".</summary>
    public static string Number => Asm.GetName().Version?.ToString(2) ?? "";

    /// <summary>"dev-minha-branch-abc1234", ou null numa versão normal.</summary>
    public static string? Dev
    {
        get
        {
            string? info = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info != null && info.StartsWith("dev-") ? info.Split('+')[0] : null;
        }
    }

    /// <summary>Rodapé do menu: "v0.16" ou "v0.16 dev · minha-branch-abc1234".</summary>
    public static string Display => Dev is string d ? $"v{Number} dev · {d[4..]}" : $"v{Number}";

    /// <summary>Para o --diag: "0.16.0.0" ou "0.16.0.0 (dev-minha-branch-abc1234)".</summary>
    public static string Full => Asm.GetName().Version + (Dev is string d ? $" ({d})" : "");
}
