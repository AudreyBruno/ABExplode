using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ABExplode.Core.Tests;

public class RtSceneTests
{
    // A cena do ray tracing tem de ser a mesma sempre (no DXR e no Vulkan): se mudar, a nota deixa de ser comparável.
    private static string Sha<T>(T[] data) where T : unmanaged =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan())))[..16];

    [Fact]
    public void Quantidades_da_cena()
    {
        var mb = RtScene.BuildMeshes();
        Assert.Equal(6, mb.Meshes.Count);
        Assert.Equal(mb.I.Count, mb.Meshes.Sum(m => (int)m.IndexCount));
        Assert.Equal(mb.V.Count, mb.Meshes.Sum(m => (int)m.VertexCount));
        Assert.All(mb.Meshes, m => Assert.Equal(0u, m.IndexCount % 3));
        // Os índices de cada malha apontam para os vértices dela (a BLAS e o shader leem a partir do início da malha)
        Assert.All(mb.Meshes, m => Assert.True(mb.I.Skip((int)m.IndexOffset).Take((int)m.IndexCount).Max() < m.VertexCount));
        var objects = RtScene.BuildObjects();
        Assert.Equal(RtScene.ObjectCount, objects.Count);
        Assert.All(objects, o => Assert.Equal((uint)o.Mesh, o.Mat.Mesh));
    }

    [Fact]
    public void Estruturas_tem_o_tamanho_do_shader()
    {
        // Layout escalar no Vulkan e StructuredBuffer no DXR: as structs do C# não podem ter enchimento
        Assert.Equal(24, Marshal.SizeOf<RtVertex>());
        Assert.Equal(16, Marshal.SizeOf<RtMeshInfo>());
        Assert.Equal(32, Marshal.SizeOf<RtMaterial>());
    }

    [Fact]
    public void Geometria_nao_muda()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Somas gravadas no Windows (MathF pode diferir no último bit em outro sistema).");
        var mb = RtScene.BuildMeshes();
        Assert.Equal("57FE9D64F292FECF", Sha(mb.V.ToArray()));
        Assert.Equal("34DAE9CB400CBE3E", Sha(mb.I.ToArray()));
        var objects = RtScene.BuildObjects();
        Assert.Equal("351D9CB9910E4B5B", Sha(objects.Select(o => o.Mat).ToArray()));
        Assert.Equal("1E9C761D6327CA8E", Sha(objects.Select(o => new Vector4(o.Pos, o.Spin)).ToArray()));
        Assert.Equal("C321E712DE3E0B0F", Sha(objects.Select(o => o.Scale3).ToArray()));
    }
}
