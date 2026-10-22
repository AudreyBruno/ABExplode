using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ABExplode.Core.Tests;

public class GameSceneTests
{
    // A cena de jogo do RESET BENCH tem de ser a mesma sempre (e no Direct3D e no Vulkan): se a geometria mudar, a nota
    // deixa de ser comparável com o histórico. As árvores e o terreno usam só inteiros e float simples, então o resultado
    // é o mesmo em qualquer sistema.
    private static string Sha<T>(T[] data) where T : unmanaged =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan())))[..16];

    [Fact]
    public void Quantidades_da_cena()
    {
        var (tv, ti) = GameScene.BuildTerrain();
        Assert.Equal(GameScene.TerrainN * GameScene.TerrainN, tv.Length);
        Assert.Equal((GameScene.TerrainN - 1) * (GameScene.TerrainN - 1) * 6, ti.Length);
        Assert.Equal(GameScene.TreeCount, GameScene.PlaceTrees().Length);
        var (wv, wi) = GameScene.BuildWater();
        Assert.Equal(4, wv.Length);
        Assert.Equal(6, wi.Length);
    }

    [Fact]
    public void Geometria_nao_muda()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Somas gravadas no Windows (MathF pode diferir no último bit em outro sistema).");
        Assert.Equal("3E1DD5B66F984625", Sha(GameScene.PlaceTrees()));
        Assert.Equal("7926583B8C7DE3F3", Sha(GameScene.BuildTerrain().V));
        Assert.Equal("2D904B2BC207F000", Sha(GameScene.BuildTree().V));
    }
}
