using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ABExplode.Core.Tests;

public class BenchRendererTests
{
    // SHA-256 dos pixels de uma passada completa, gravado com o BenchRenderer antes de ele ir para o Core (v0.16).
    // As sementes são fixas por pixel, então a imagem não depende do número de threads nem da ordem dos blocos.
    // Se este teste falhar, a cena, o renderizador ou a matemática mudou e a nota do RESET BENCH deixou de ser
    // comparável com o histórico.
    private const string ExpectedImageHash = "BBEF9A915A03168B0F6D93304EC35CA57B93D0D8284684B6209E436AC4DD8501";

    [Fact]
    public void Render_produz_a_mesma_imagem_de_referencia()
    {
        var r = new BenchRenderer();
        var res = r.Run(Environment.ProcessorCount, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(1, res.Passes);
        Assert.Equal((long)BenchRenderer.Width * BenchRenderer.Height * BenchRenderer.Spp, res.Samples);
        // MathF.Sin/Cos/Pow/Tan vêm da biblioteca C do sistema e podem diferir no último bit entre Windows e Linux.
        // A referência foi gravada no Windows; a do Linux entra na Fase 4 (MIGRACAO-AVALONIA.md).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Imagem de referência gravada só no Windows.");
        string hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(r.Pixels.AsSpan())));
        Assert.Equal(ExpectedImageHash, hash);
    }
}
