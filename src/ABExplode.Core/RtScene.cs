using System.Numerics;
using System.Runtime.InteropServices;

namespace ABExplode.Core;

/// <summary>Uma passada medida do teste de ray tracing.</summary>
public sealed record RtPass(double AvgFps, double Low1Fps, int Frames);

/// <summary>
/// Resultado do teste de ray tracing: nota pela MEDIANA das passadas (resiste a uma passada ruim) e variação entre elas,
/// que diz se o resultado é confiável para comparar.
/// </summary>
public sealed record GpuRtResult(IReadOnlyList<RtPass> Passes, int Width, int Height, string Preset, string Adapter, double GigaRaysPerSec)
{
    public double MedianFps => Median(Passes.Select(p => p.AvgFps));
    public double MedianLow1 => Median(Passes.Select(p => p.Low1Fps));
    /// <summary>(maior − menor) ÷ mediana, em %.</summary>
    public double VariationPct => Passes.Count < 2 || MedianFps <= 0 ? 0 : (Passes.Max(p => p.AvgFps) - Passes.Min(p => p.AvgFps)) / MedianFps * 100;
    public bool Reliable => Passes.Count >= 3 && VariationPct < 3;
    /// <summary>Nota = FPS mediano × 10 (mesma escala da cena de jogo).</summary>
    public double Score => MedianFps * 10;

    private static double Median(IEnumerable<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}

[StructLayout(LayoutKind.Sequential)] public struct RtVertex { public Vector3 Pos; public Vector3 Nrm; }
[StructLayout(LayoutKind.Sequential)] public struct RtMeshInfo { public uint IndexOffset, VertexOffset, Pad0, Pad1; }
[StructLayout(LayoutKind.Sequential)] public struct RtMaterial { public Vector3 Albedo; public float Metal; public float Rough; public float Emissive; public uint Mesh; public float Pad; }

/// <summary>Um objeto da cena: malha, posição, escala, giro e material.</summary>
public sealed record RtObject(int Mesh, Vector3 Pos, float Scale, Vector3 Scale3, float Spin, Vector3 Axis, RtMaterial Mat);

/// <summary>Malhas da cena num buffer só: cada uma é um trecho de V e I.</summary>
public sealed class RtMeshes
{
    public readonly List<RtVertex> V = new();
    public readonly List<uint> I = new();
    public readonly List<(uint IndexOffset, uint IndexCount, uint VertexOffset, uint VertexCount)> Meshes = new();
    private int _vStart, _iStart;
    internal void Begin() { _vStart = V.Count; _iStart = I.Count; }
    internal void End() => Meshes.Add(((uint)_iStart, (uint)(I.Count - _iStart), (uint)_vStart, (uint)(V.Count - _vStart)));
    internal uint Add(Vector3 p, Vector3 n) { V.Add(new RtVertex { Pos = p, Nrm = Vector3.Normalize(n) }); return (uint)(V.Count - 1 - _vStart); }
    internal void Tri(uint a, uint b, uint c) { I.Add(a); I.Add(b); I.Add(c); }
}

/// <summary>
/// Cena do teste de ray tracing do RESET BENCH, igual no DXR (Windows) e no Vulkan (Linux): ~4 mil objetos (esferas, toros,
/// colunas, caixas) em materiais fosco, metal e espelho, mais luzes emissivas, e um trajeto fixo de câmera. Semente fixa:
/// a mesma cena em qualquer PC.
/// </summary>
public static class RtScene
{
    public const int MeshGround = 0, MeshSphere = 1, MeshHeroSphere = 2, MeshTorus = 3, MeshColumn = 4, MeshBox = 5;
    public const int ObjectCount = 4000;
    public const float World = 220;

    public static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(-0.4f, -0.75f, -0.35f));
    public static Matrix4x4 Projection(int width, int height) => Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, width / (float)height, 0.1f, 2000);

    /// <summary>Amostras por pixel e rebatidas de cada preset.</summary>
    public static (uint Spp, uint Bounces) Quality(string preset) => preset switch
    {
        "normal" => (2u, 2u),
        "extrema" => (8u, 4u),
        _ => (4u, 3u),
    };

    /// <summary>Raios por pixel: por amostra e rebatida, 1 de visibilidade/rebatida + 1 sombra + 1 oclusão.</summary>
    public static double RaysPerPixel(string preset)
    {
        var (spp, bounces) = Quality(preset);
        return spp * bounces * 3.0;
    }

    // ------------------------------------------------------------------ malhas

    /// <summary>Chão, esfera, esfera grande (~25 mil triângulos), toro, coluna com caneluras e caixa, nesta ordem (Mesh*).</summary>
    public static RtMeshes BuildMeshes()
    {
        var mb = new RtMeshes();
        Plane(mb, World + 60, 96);
        Sphere(mb, 32);
        Sphere(mb, 160);
        Torus(mb, 48, 24, 1, 0.35f);
        Column(mb, 48);
        Box(mb);
        return mb;
    }

    private static void Plane(RtMeshes b, float size, int n)
    {
        b.Begin();
        for (int z = 0; z <= n; z++)
            for (int x = 0; x <= n; x++)
                b.Add(new((x / (float)n - 0.5f) * size, 0, (z / (float)n - 0.5f) * size), Vector3.UnitY);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                uint a = (uint)(z * (n + 1) + x), c = a + (uint)(n + 1);
                b.Tri(a, c, a + 1); b.Tri(a + 1, c, c + 1);
            }
        b.End();
    }

    private static void Sphere(RtMeshes b, int seg)
    {
        b.Begin();
        int rings = seg / 2;
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= seg; s++)
            {
                float th = MathF.PI * r / rings, ph = 2 * MathF.PI * s / seg;
                var p = new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph));
                b.Add(p, p);
            }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                uint a = (uint)(r * (seg + 1) + s), c = a + (uint)(seg + 1);
                b.Tri(a, a + 1, c); b.Tri(a + 1, c + 1, c);
            }
        b.End();
    }

    private static void Torus(RtMeshes b, int seg, int side, float R, float r)
    {
        b.Begin();
        for (int i = 0; i <= seg; i++)
            for (int j = 0; j <= side; j++)
            {
                float u = 2 * MathF.PI * i / seg, v = 2 * MathF.PI * j / side;
                var c = new Vector3(MathF.Cos(u) * R, 0, MathF.Sin(u) * R);
                var n = new Vector3(MathF.Cos(u) * MathF.Cos(v), MathF.Sin(v), MathF.Sin(u) * MathF.Cos(v));
                b.Add(c + n * r, n);
            }
        for (int i = 0; i < seg; i++)
            for (int j = 0; j < side; j++)
            {
                uint a = (uint)(i * (side + 1) + j), c = a + (uint)(side + 1);
                b.Tri(a, c, a + 1); b.Tri(a + 1, c, c + 1);
            }
        b.End();
    }

    private static void Column(RtMeshes b, int seg)
    {
        b.Begin();
        for (int i = 0; i <= seg; i++)
        {
            float a = 2 * MathF.PI * i / seg;
            // Caneluras: o raio ondula como numa coluna grega (mais triângulos e normais variadas).
            float rr = 1 + 0.06f * MathF.Cos(a * 12);
            var n = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            b.Add(new Vector3(MathF.Cos(a) * rr, 0, MathF.Sin(a) * rr), n);
            b.Add(new Vector3(MathF.Cos(a) * rr, 1, MathF.Sin(a) * rr), n);
        }
        for (int i = 0; i < seg; i++) { uint a = (uint)(i * 2); b.Tri(a, a + 1, a + 2); b.Tri(a + 1, a + 3, a + 2); }
        uint top = b.Add(new Vector3(0, 1, 0), Vector3.UnitY);
        for (int i = 0; i < seg; i++)
        {
            float a0 = 2 * MathF.PI * i / seg, a1 = 2 * MathF.PI * (i + 1) / seg;
            uint p0 = b.Add(new Vector3(MathF.Cos(a0), 1, MathF.Sin(a0)), Vector3.UnitY);
            uint p1 = b.Add(new Vector3(MathF.Cos(a1), 1, MathF.Sin(a1)), Vector3.UnitY);
            b.Tri(top, p1, p0);
        }
        b.End();
    }

    private static void Box(RtMeshes b)
    {
        b.Begin();
        Vector3[] n = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (var f in n)
        {
            var u = MathF.Abs(f.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var v = Vector3.Cross(f, u);
            uint a = b.Add(f - u - v, f), c = b.Add(f + u - v, f), d = b.Add(f + u + v, f), e = b.Add(f - u + v, f);
            b.Tri(a, c, d); b.Tri(a, d, e);
        }
        b.End();
    }

    // ------------------------------------------------------------------ objetos e câmera

    /// <summary>Objetos espalhados com semente fixa: a mesma cena em qualquer PC.</summary>
    public static List<RtObject> BuildObjects()
    {
        var rnd = new Random(20261015);
        float R() => (float)rnd.NextDouble();
        var list = new List<RtObject>
        {
            new(MeshGround, Vector3.Zero, 1, Vector3.One, 0, Vector3.UnitY,
                new RtMaterial { Albedo = new(0.55f, 0.55f, 0.58f), Metal = 0.15f, Rough = 0.35f, Mesh = MeshGround }),
        };
        // Esferas-espelho e douradas grandes no centro (reflexos de reflexos).
        for (int i = 0; i < 7; i++)
        {
            float a = i / 7f * MathF.Tau;
            var pos = new Vector3(MathF.Cos(a) * 16, 6, MathF.Sin(a) * 16);
            bool gold = i % 2 == 1;
            list.Add(new(MeshHeroSphere, pos, 6, new Vector3(6), 0, Vector3.UnitY, new RtMaterial
            {
                Albedo = gold ? new(1.0f, 0.78f, 0.34f) : new(0.95f, 0.95f, 0.97f), Metal = 1, Rough = gold ? 0.12f : 0.0f, Mesh = MeshHeroSphere,
            }));
        }
        while (list.Count < ObjectCount)
        {
            var pos = new Vector3((R() - 0.5f) * World, 0, (R() - 0.5f) * World);
            if (new Vector2(pos.X, pos.Z).Length() < 26) continue; // deixa o centro livre para as esferas grandes
            int mesh = R() switch { < 0.35f => MeshSphere, < 0.55f => MeshTorus, < 0.75f => MeshColumn, _ => MeshBox };
            float s = 0.6f + R() * 1.8f;
            Vector3 s3 = mesh == MeshColumn ? new(s * 0.6f, s * (2 + R() * 3), s * 0.6f) : new(s);
            pos.Y = mesh switch { MeshSphere => s, MeshTorus => s * 0.35f + 0.6f, MeshBox => s, _ => 0 };
            float roll = R();
            var mat = new RtMaterial
            {
                Albedo = Palette(R(), R()),
                Metal = roll < 0.3f ? 1 : 0, Rough = roll < 0.3f ? R() * 0.35f : 0.6f + R() * 0.4f,
                Emissive = roll > 0.97f ? 6 + R() * 10 : 0, Mesh = (uint)mesh,
            };
            float spin = mesh is MeshTorus or MeshBox && R() < 0.5f ? 0.3f + R() * 1.2f : 0;
            var axis = Vector3.Normalize(new Vector3(R() - 0.5f, 1, R() - 0.5f));
            list.Add(new(mesh, pos, s, s3, spin, axis, mat));
        }
        return list;
    }

    /// <summary>Cores saturadas de uma paleta fixa, com brilho variado.</summary>
    private static Vector3 Palette(float pick, float shade)
    {
        Vector3[] p = { new(0.85f, 0.18f, 0.12f), new(0.95f, 0.55f, 0.08f), new(0.12f, 0.45f, 0.85f), new(0.1f, 0.65f, 0.4f),
                        new(0.9f, 0.85f, 0.75f), new(0.45f, 0.2f, 0.7f), new(0.08f, 0.08f, 0.1f), new(0.75f, 0.75f, 0.78f) };
        return p[(int)(pick * p.Length) % p.Length] * (0.55f + shade * 0.45f);
    }

    /// <summary>Matriz do objeto no instante t (segundos): escala, giro e posição.</summary>
    public static Matrix4x4 ObjMatrix(RtObject o, float t) =>
        Matrix4x4.CreateScale(o.Scale3) *
        (o.Spin != 0 ? Matrix4x4.CreateFromAxisAngle(o.Axis, t * o.Spin) : Matrix4x4.Identity) *
        Matrix4x4.CreateTranslation(o.Pos);

    /// <summary>Câmera em trajeto fixo pelo tempo da passada (0..1): mesma sequência de imagens em qualquer placa.</summary>
    public static (Vector3 Eye, Vector3 Target) CameraAt(float t)
    {
        float a = t * MathF.Tau * 0.75f + 0.4f;
        float r = 40 + 26 * MathF.Sin(t * MathF.PI);
        var eye = new Vector3(MathF.Cos(a) * r, 12 + 10 * (0.5f + 0.5f * MathF.Sin(t * MathF.Tau * 1.5f)), MathF.Sin(a) * r);
        var target = new Vector3(MathF.Cos(a + 0.6f) * 6, 3, MathF.Sin(a + 0.6f) * 6);
        return (eye, target);
    }
}
