using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ABExplode.Core;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace ABExplode.Platform.Windows;

/// <summary>
/// ABExplode · Ray tracing (DXR 1.1, Direct3D 12): usa os núcleos de RT da placa (RTX, Radeon RX 6000+, Arc).
/// Cena procedural com ~4 mil objetos (esferas, toros, colunas, caixas) em materiais fosco, metal e espelho, mais luzes
/// emissivas. Por pixel, um path tracer com raios inline (RayQuery num compute shader): visibilidade, sombra suave do sol,
/// oclusão de ambiente e rebatidas de reflexo/iluminação global. A estrutura de aceleração do topo (TLAS) é refeita a cada
/// frame com objetos girando, como nos jogos. O tempo de frame vem dos timestamps da própria GPU.
/// Para comparar com confiança: aquecimento sem medir, depois 3 passadas iguais; nota pela mediana e variação entre passadas.
/// </summary>
public sealed class GpuRtBench
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public string Preset { get; init; } = "alta";     // normal | alta | extrema
    public TimeSpan PassDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int Passes { get; init; } = 3;
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(10);
    public string AdapterName { get; init; } = "";

    public double Progress { get; private set; }
    public double CurrentFps { get; private set; }
    public string Phase { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Cancelled { get; private set; }

    private volatile bool _stop;
    private WndProc? _wndProc;
    public void Cancel() => _stop = true;

    /// <summary>Verifica se a placa tem ray tracing por hardware (DXR 1.1). Retorna null se tiver, senão o motivo.</summary>
    public static string? CheckSupport(string adapterName)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var adapter = PickAdapter(factory, adapterName, out _);
            using var device = D3D12.D3D12CreateDevice<ID3D12Device5>(adapter, FeatureLevel.Level_12_0);
            return device.Options5.RaytracingTier >= RaytracingTier.Tier1_1 ? null
                 : "Esta placa não tem ray tracing por hardware (DXR 1.1). Precisa de RTX 20 ou mais nova, Radeon RX 6000 ou mais nova, ou Intel Arc.";
        }
        catch (Exception ex) { return $"Direct3D 12 indisponível: {ex.Message}"; }
    }

    private static IDXGIAdapter1 PickAdapter(IDXGIFactory4 factory, string name, out string desc)
    {
        var list = new List<IDXGIAdapter1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
        {
            if ((a.Description1.Flags & AdapterFlags.Software) == 0) list.Add(a); else a.Dispose();
        }
        if (list.Count == 0) throw new InvalidOperationException("Nenhuma placa de vídeo encontrada.");
        var pick = list.FirstOrDefault(a => name.Length > 0 && a.Description1.Description.Contains(name, StringComparison.OrdinalIgnoreCase))
                   ?? list.OrderByDescending(a => a.Description1.DedicatedVideoMemory).First();
        foreach (var a in list) if (a != pick) a.Dispose();
        desc = pick.Description1.Description;
        return pick;
    }

    // ------------------------------------------------------------------ estruturas da GPU (a cena está em Core/RtScene)

    /// <summary>D3D12_RAYTRACING_INSTANCE_DESC (64 bytes): matriz 3x4, id(24)+máscara(8), contribuição(24)+flags(8), BLAS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceDesc
    {
        public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
        public uint IdMask, ContribFlags;
        public ulong Blas;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CamPos; public float Time;
        public Vector3 SunDir; public uint FrameIndex;
        public uint Width, Height, Spp, Bounces;
    }

    // ------------------------------------------------------------------ shader (HLSL SM 6.5, raios inline)

    private const string Hlsl = """
#define RS "CBV(b0), SRV(t0), SRV(t1), SRV(t2), SRV(t3), SRV(t4), DescriptorTable(UAV(u0))"
struct Frame { row_major float4x4 InvViewProj; float3 CamPos; float Time; float3 SunDir; uint FrameIndex; uint Width; uint Height; uint Spp; uint Bounces; };
ConstantBuffer<Frame> F : register(b0);
RaytracingAccelerationStructure Scene : register(t0);
struct Vtx { float3 pos; float3 nrm; };
StructuredBuffer<Vtx> Verts : register(t1);
StructuredBuffer<uint> Indices : register(t2);
struct Mesh { uint indexOffset; uint vertexOffset; uint p0; uint p1; };
StructuredBuffer<Mesh> Meshes : register(t3);
struct Inst { float3 albedo; float metal; float rough; float emissive; uint mesh; float pad; };
StructuredBuffer<Inst> Insts : register(t4);
RWTexture2D<float4> Output : register(u0);

uint Pcg(inout uint s) { s = s * 747796405u + 2891336453u; uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u; return (w >> 22u) ^ w; }
float Rnd(inout uint s) { return (Pcg(s) & 0xFFFFFF) / 16777216.0; }

float3 CosineDir(float3 n, inout uint s)
{
    float u = Rnd(s), v = Rnd(s), r = sqrt(u), phi = 6.2831853 * v;
    float3 t = abs(n.y) < 0.99 ? normalize(cross(n, float3(0, 1, 0))) : float3(1, 0, 0);
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0, 1 - u)));
}

float3 Sky(float3 d)
{
    float3 sky = lerp(float3(0.85, 0.9, 1.0), float3(0.25, 0.45, 0.85), saturate(d.y));
    float sun = pow(saturate(dot(d, -F.SunDir)), 900) * 60;
    return sky * 0.8 + sun * float3(1.0, 0.92, 0.8);
}

struct Hit { float3 pos; float3 nrm; Inst mat; };

bool Closest(float3 o, float3 d, out Hit h)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    RayDesc r; r.Origin = o; r.TMin = 0.002; r.Direction = d; r.TMax = 2000;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, r);
    q.Proceed();
    h = (Hit)0;
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT) return false;
    Inst m = Insts[q.CommittedInstanceID()];
    Mesh me = Meshes[m.mesh];
    uint i0 = me.indexOffset + q.CommittedPrimitiveIndex() * 3;
    float2 bc = q.CommittedTriangleBarycentrics();
    float3 n0 = Verts[me.vertexOffset + Indices[i0]].nrm, n1 = Verts[me.vertexOffset + Indices[i0 + 1]].nrm, n2 = Verts[me.vertexOffset + Indices[i0 + 2]].nrm;
    float3 n = n0 * (1 - bc.x - bc.y) + n1 * bc.x + n2 * bc.y;
    float3x4 o2w = q.CommittedObjectToWorld3x4();
    n = normalize(mul((float3x3)o2w, n));
    if (dot(n, d) > 0) n = -n;
    h.pos = o + d * q.CommittedRayT() + n * 0.003;
    h.nrm = n; h.mat = m;
    return true;
}

bool Occluded(float3 o, float3 d, float tmax)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> q;
    RayDesc r; r.Origin = o; r.TMin = 0.002; r.Direction = d; r.TMax = tmax;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, r);
    q.Proceed();
    return q.CommittedStatus() != COMMITTED_NOTHING;
}

[RootSignature(RS)]
[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= F.Width || id.y >= F.Height) return;
    uint seed = (id.y * F.Width + id.x) * 9781u + F.FrameIndex * 6271u;
    float3 color = 0;
    for (uint s = 0; s < F.Spp; s++)
    {
        float2 uv = (float2(id.xy) + float2(Rnd(seed), Rnd(seed))) / float2(F.Width, F.Height);
        float4 p = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, 1, 1), F.InvViewProj);
        float3 o = F.CamPos, d = normalize(p.xyz / p.w - F.CamPos);
        float3 through = 1, acc = 0;
        for (uint b = 0; b < F.Bounces; b++)
        {
            Hit h;
            if (!Closest(o, d, h)) { acc += through * Sky(d); break; }
            acc += through * h.mat.albedo * h.mat.emissive;
            // Sombra suave do sol (direção sorteada num cone) + oclusão de ambiente (um raio no hemisfério).
            float3 sd = normalize(-F.SunDir + (float3(Rnd(seed), Rnd(seed), Rnd(seed)) - 0.5) * 0.06);
            float ndl = saturate(dot(h.nrm, sd));
            float sunVis = ndl > 0 && !Occluded(h.pos, sd, 1000) ? 1 : 0;
            float3 ad = CosineDir(h.nrm, seed);
            float ao = Occluded(h.pos, ad, 6) ? 0.15 : 1;
            float3 diffuse = h.mat.albedo * (1 - h.mat.metal);
            acc += through * diffuse * (ndl * sunVis * float3(3.6, 3.3, 2.9) + ao * float3(0.12, 0.15, 0.2));
            // Próxima rebatida: reflexo (metal) ou iluminação indireta (difuso).
            if (h.mat.metal > 0.5)
            {
                float3 rd = reflect(d, h.nrm);
                d = normalize(rd + (CosineDir(h.nrm, seed) - h.nrm) * h.mat.rough);
                through *= h.mat.albedo;
            }
            else
            {
                d = CosineDir(h.nrm, seed);
                through *= h.mat.albedo * 0.8;
            }
            o = h.pos;
            if (max(through.x, max(through.y, through.z)) < 0.02) break;
        }
        color += acc;
    }
    color /= F.Spp;
    color = color / (1 + color);               // tone mapping (Reinhard)
    color = pow(saturate(color), 1 / 2.2);     // gama
    Output[id.xy] = float4(color, 1);
}
""";

    // ------------------------------------------------------------------ execução

    public GpuRtResult? Run()
    {
        var oldPriority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        IntPtr hwnd = IntPtr.Zero;
        string cls = "ABExplodeRtBench" + Environment.TickCount64;
        IntPtr hInst = GetModuleHandleW(null);
        var keep = new List<IDisposable>();
        T K<T>(T d) where T : IDisposable { keep.Add(d); return d; }
        ID3D12CommandQueue? queue = null; ID3D12Fence? fence = null; ulong fenceValue = 0;
        var fenceEvent = new AutoResetEvent(false);

        void WaitGpu(ulong v)
        {
            if (fence!.CompletedValue >= v) return;
            fence.SetEventOnCompletion(v, fenceEvent.SafeWaitHandle.DangerousGetHandle());
            fenceEvent.WaitOne();
        }

        try
        {
            // ---- Janela ----
            _wndProc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst, hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512), lpszClassName = cls
            };
            RegisterClassExW(ref wc);
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            double scale = Math.Min(1.0, Math.Min(sw * 0.85 / Width, sh * 0.85 / Height));
            var rect = new RECT { Right = (int)(Width * scale), Bottom = (int)(Height * scale) };
            const uint style = 0x00CF0000 | 0x10000000;
            AdjustWindowRect(ref rect, style, false);
            int ww = rect.Right - rect.Left, wh = rect.Bottom - rect.Top;
            hwnd = CreateWindowExW(0, cls, "ABExplode · Ray tracing", style, (sw - ww) / 2, (sh - wh) / 2, ww, wh,
                                   IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

            // ---- Dispositivo ----
            var factory = K(DXGI.CreateDXGIFactory1<IDXGIFactory4>());
            var adapter = K(PickAdapter(factory, AdapterName, out string adapterName));
            var device = K(D3D12.D3D12CreateDevice<ID3D12Device5>(adapter, FeatureLevel.Level_12_0));
            if (device.Options5.RaytracingTier < RaytracingTier.Tier1_1)
                throw new NotSupportedException("Esta placa não tem ray tracing por hardware (DXR 1.1).");

            queue = K(device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct)));
            fence = K(device.CreateFence(0, FenceFlags.None));
            queue.GetTimestampFrequency(out ulong tsFreq);

            var swap = K(factory.CreateSwapChainForHwnd(queue, hwnd,
                new SwapChainDescription1((uint)Width, (uint)Height, Format.R8G8B8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                                          Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, SwapChainFlags.None)).QueryInterface<IDXGISwapChain3>());
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            var backBuffers = new[] { K(swap.GetBuffer<ID3D12Resource>(0)), K(swap.GetBuffer<ID3D12Resource>(1)) };

            // ---- Shader ----
            using var compiled = DxcCompiler.Compile(DxcShaderStage.Compute, Hlsl, "Main",
                new DxcCompilerOptions { ShaderModel = new DxcShaderModel(6, 5), OptimizationLevel = 3 });
            var bytecode = compiled.GetObjectBytecodeMemory().ToArray();
            if (bytecode.Length == 0) throw new InvalidOperationException("Falha ao compilar o shader: " + compiled.GetErrors());
            var rootSig = K(device.CreateRootSignature(bytecode));
            var pso = K(device.CreateComputePipelineState<ID3D12PipelineState>(new ComputePipelineStateDescription
            {
                RootSignature = rootSig, ComputeShader = bytecode,
            }));

            // ---- Comandos (um alocador por frame em voo) ----
            const int InFlight = 4;
            var allocs = new ID3D12CommandAllocator[InFlight];
            for (int i = 0; i < InFlight; i++) allocs[i] = K(device.CreateCommandAllocator(CommandListType.Direct));
            var cl = K(device.CreateCommandList<ID3D12GraphicsCommandList4>(0, CommandListType.Direct, allocs[0], null));

            ID3D12Resource Buffer(ulong size, HeapType heap, ResourceStates state, ResourceFlags flags = ResourceFlags.None) =>
                K(device.CreateCommittedResource(heap, ResourceDescription.Buffer(Math.Max(size, 256), flags), state, null));

            ID3D12Resource Upload<T>(T[] data) where T : unmanaged
            {
                ulong size = (ulong)(data.Length * Marshal.SizeOf<T>());
                var up = Buffer(size, HeapType.Upload, ResourceStates.GenericRead);
                unsafe
                {
                    void* p; up.Map(0, null, &p);
                    fixed (T* src = data) System.Buffer.MemoryCopy(src, p, (long)size, (long)size);
                    up.Unmap(0);
                }
                return up;
            }

            ID3D12Resource ToGpu<T>(T[] data) where T : unmanaged
            {
                var up = Upload(data);
                ulong size = (ulong)(data.Length * Marshal.SizeOf<T>());
                var gpu = Buffer(size, HeapType.Default, ResourceStates.Common);
                cl.CopyBufferRegion(gpu, 0, up, 0, size);
                cl.ResourceBarrierTransition(gpu, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
                return gpu;
            }

            // ---- Geometria ----
            var mb = RtScene.BuildMeshes();
            var vb = ToGpu(mb.V.ToArray());
            var ib = ToGpu(mb.I.ToArray());
            var meshInfo = ToGpu(mb.Meshes.Select(m => new RtMeshInfo { IndexOffset = m.IndexOffset, VertexOffset = m.VertexOffset }).ToArray());
            var objects = RtScene.BuildObjects();
            var mats = ToGpu(objects.Select(o => o.Mat).ToArray());

            // ---- BLAS: uma por malha, montada uma vez ----
            var blas = new ID3D12Resource[mb.Meshes.Count];
            for (int m = 0; m < mb.Meshes.Count; m++)
            {
                var (io, ic, vo, vc) = mb.Meshes[m];
                var geo = new RaytracingGeometryDescription(new RaytracingGeometryTrianglesDescription(
                    new GpuVirtualAddressAndStride(vb.GPUVirtualAddress + vo * (ulong)Marshal.SizeOf<RtVertex>(), (ulong)Marshal.SizeOf<RtVertex>()),
                    Format.R32G32B32_Float, vc, 0, ib.GPUVirtualAddress + io * 4UL, Format.R32_UInt, ic), RaytracingGeometryFlags.Opaque);
                var inputs = new BuildRaytracingAccelerationStructureInputs
                {
                    Type = RaytracingAccelerationStructureType.BottomLevel, Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                    Layout = ElementsLayout.Array, DescriptorsCount = 1, GeometryDescriptions = new[] { geo },
                };
                var info = device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
                var scratch = Buffer(info.ScratchDataSizeInBytes, HeapType.Default, ResourceStates.Common, ResourceFlags.AllowUnorderedAccess);
                blas[m] = Buffer(info.ResultDataMaxSizeInBytes, HeapType.Default, ResourceStates.RaytracingAccelerationStructure, ResourceFlags.AllowUnorderedAccess);
                cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(blas[m].GPUVirtualAddress, inputs, 0, scratch.GPUVirtualAddress));
                cl.ResourceBarrierUnorderedAccessView(blas[m]);
            }

            // ---- TLAS: refeita a cada frame (objetos girando) ----
            var tlasInputs = new BuildRaytracingAccelerationStructureInputs
            {
                Type = RaytracingAccelerationStructureType.TopLevel, Flags = RaytracingAccelerationStructureBuildFlags.PreferFastBuild,
                Layout = ElementsLayout.Array, DescriptorsCount = (uint)objects.Count,
            };
            var tlasInfo = device.GetRaytracingAccelerationStructurePrebuildInfo(tlasInputs);
            var tlas = Buffer(tlasInfo.ResultDataMaxSizeInBytes, HeapType.Default, ResourceStates.RaytracingAccelerationStructure, ResourceFlags.AllowUnorderedAccess);
            var tlasScratch = Buffer(tlasInfo.ScratchDataSizeInBytes, HeapType.Default, ResourceStates.Common, ResourceFlags.AllowUnorderedAccess);
            int instSize = Marshal.SizeOf<InstanceDesc>();
            var instUpload = new ID3D12Resource[InFlight];
            var instPtr = new IntPtr[InFlight];
            for (int i = 0; i < InFlight; i++)
            {
                instUpload[i] = Buffer((ulong)(objects.Count * instSize), HeapType.Upload, ResourceStates.GenericRead);
                unsafe { void* p; instUpload[i].Map(0, null, &p); instPtr[i] = (IntPtr)p; }
            }
            var cbUpload = new ID3D12Resource[InFlight];
            var cbPtr = new IntPtr[InFlight];
            for (int i = 0; i < InFlight; i++)
            {
                cbUpload[i] = Buffer(256, HeapType.Upload, ResourceStates.GenericRead);
                unsafe { void* p; cbUpload[i].Map(0, null, &p); cbPtr[i] = (IntPtr)p; }
            }

            // ---- Saída (UAV) ----
            var output = K(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)Width, (uint)Height, 1, 1, 1, 0, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess, null));
            var heap = K(device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible)));
            device.CreateUnorderedAccessView(output, null, null, heap.GetCPUDescriptorHandleForHeapStart());

            // ---- Timestamps da GPU (um por frame em voo) ----
            var tsHeap = K(device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, InFlight)));
            var tsReadback = Buffer(8UL * InFlight, HeapType.Readback, ResourceStates.CopyDest);

            // Envia a montagem da cena e espera terminar antes de medir.
            cl.Close();
            queue.ExecuteCommandList(cl);
            queue.Signal(fence, ++fenceValue);
            WaitGpu(fenceValue);

            var (spp, bounces) = RtScene.Quality(Preset);
            var proj = RtScene.Projection(Width, Height);
            var sunDir = RtScene.SunDir;
            var frameFence = new ulong[InFlight];
            var passes = new List<RtPass>();
            var inst = new InstanceDesc[objects.Count];
            uint frameIndex = 0;
            int totalPhases = Passes + (Warmup > TimeSpan.Zero ? 1 : 0);

            for (int phase = 0; phase < totalPhases && !_stop; phase++)
            {
                bool warm = Warmup > TimeSpan.Zero && phase == 0;
                var duration = warm ? Warmup : PassDuration;
                Phase = warm ? "Aquecendo" : $"Passada {(warm ? 0 : phase - (Warmup > TimeSpan.Zero ? 1 : 0)) + 1} de {Passes}";
                var frameTimes = new List<double>(20000);
                var clock = Stopwatch.StartNew();
                double lastPresent = 0, lastTitle = 0; int titleFrames = 0;
                ulong lastTs = 0; long frame = 0;

                while (!_stop && clock.Elapsed < duration)
                {
                    while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
                    if (_stop) break;

                    int slot = (int)(frame % InFlight);
                    if (frame >= InFlight)
                    {
                        WaitGpu(frameFence[slot]);
                        // Tempo entre o fim de dois frames seguidos, no relógio da GPU.
                        ulong ts;
                        unsafe
                        {
                            void* p; var range = new Vortice.Direct3D12.Range((nuint)(slot * 8), (nuint)(slot * 8 + 8));
                            tsReadback.Map(0, range, &p); ts = *(ulong*)((byte*)p + slot * 8);
                            tsReadback.Unmap(0, new Vortice.Direct3D12.Range(0, 0));
                        }
                        if (lastTs != 0 && ts > lastTs) frameTimes.Add((ts - lastTs) * 1000.0 / tsFreq);
                        lastTs = ts;
                        titleFrames++;
                    }

                    // Câmera e objetos pelo tempo da passada: a mesma cena no mesmo instante em qualquer PC.
                    float t = (float)(clock.Elapsed.TotalSeconds / duration.TotalSeconds);
                    float secs = (float)clock.Elapsed.TotalSeconds;
                    var (eye, target) = RtScene.CameraAt(t);
                    var vp = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * proj;
                    Matrix4x4.Invert(vp, out var inv);
                    var cbData = new FrameCB
                    {
                        InvViewProj = inv, CamPos = eye, Time = secs, SunDir = sunDir, FrameIndex = frameIndex++,
                        Width = (uint)Width, Height = (uint)Height, Spp = spp, Bounces = bounces,
                    };
                    Marshal.StructureToPtr(cbData, cbPtr[slot], false);
                    for (int i = 0; i < objects.Count; i++)
                    {
                        var o = objects[i];
                        var m = RtScene.ObjMatrix(o, secs);
                        inst[i] = new InstanceDesc
                        {
                            M00 = m.M11, M01 = m.M21, M02 = m.M31, M03 = m.M41,
                            M10 = m.M12, M11 = m.M22, M12 = m.M32, M13 = m.M42,
                            M20 = m.M13, M21 = m.M23, M22 = m.M33, M23 = m.M43,
                            IdMask = (uint)i | 0xFF000000u, ContribFlags = 0x05000000u, // CULL_DISABLE | FORCE_OPAQUE
                            Blas = blas[o.Mesh].GPUVirtualAddress,
                        };
                    }
                    unsafe { fixed (InstanceDesc* src = inst) System.Buffer.MemoryCopy(src, (void*)instPtr[slot], (long)inst.Length * instSize, (long)inst.Length * instSize); }

                    allocs[slot].Reset();
                    cl.Reset(allocs[slot], pso);
                    tlasInputs.InstanceDescriptions = instUpload[slot].GPUVirtualAddress;
                    cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(tlas.GPUVirtualAddress, tlasInputs, 0, tlasScratch.GPUVirtualAddress));
                    cl.ResourceBarrierUnorderedAccessView(tlas);

                    cl.SetComputeRootSignature(rootSig);
                    cl.SetDescriptorHeaps(heap);
                    cl.SetComputeRootConstantBufferView(0, cbUpload[slot].GPUVirtualAddress);
                    cl.SetComputeRootShaderResourceView(1, tlas.GPUVirtualAddress);
                    cl.SetComputeRootShaderResourceView(2, vb.GPUVirtualAddress);
                    cl.SetComputeRootShaderResourceView(3, ib.GPUVirtualAddress);
                    cl.SetComputeRootShaderResourceView(4, meshInfo.GPUVirtualAddress);
                    cl.SetComputeRootShaderResourceView(5, mats.GPUVirtualAddress);
                    cl.SetComputeRootDescriptorTable(6, heap.GetGPUDescriptorHandleForHeapStart());
                    cl.Dispatch((uint)((Width + 7) / 8), (uint)((Height + 7) / 8), 1);
                    cl.ResourceBarrierUnorderedAccessView(output);

                    // Mostra na janela a ~30 Hz (cópia barata); o resto dos frames só é calculado.
                    double now = clock.Elapsed.TotalSeconds;
                    bool present = now - lastPresent >= 1 / 30.0;
                    ID3D12Resource? bb = null;
                    if (present)
                    {
                        bb = backBuffers[swap.CurrentBackBufferIndex];
                        cl.ResourceBarrierTransition(output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                        cl.ResourceBarrierTransition(bb, ResourceStates.Present, ResourceStates.CopyDest);
                        cl.CopyResource(bb, output);
                        cl.ResourceBarrierTransition(bb, ResourceStates.CopyDest, ResourceStates.Present);
                        cl.ResourceBarrierTransition(output, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
                    }
                    cl.EndQuery(tsHeap, QueryType.Timestamp, (uint)slot);
                    cl.ResolveQueryData(tsHeap, QueryType.Timestamp, (uint)slot, 1, tsReadback, (ulong)(slot * 8));
                    cl.Close();
                    queue.ExecuteCommandList(cl);
                    if (present) { swap.Present(0, PresentFlags.None); lastPresent = now; }
                    queue.Signal(fence, ++fenceValue);
                    frameFence[slot] = fenceValue;
                    frame++;

                    if (now - lastTitle >= 0.5)
                    {
                        CurrentFps = titleFrames / (now - lastTitle);
                        titleFrames = 0; lastTitle = now;
                        double done = phase + now / duration.TotalSeconds;
                        Progress = done / totalPhases;
                        SetWindowTextW(hwnd, $"ABExplode · Ray tracing · {Width}x{Height} · {Preset} · {Phase} · {CurrentFps:0} FPS");
                    }
                }
                WaitGpu(fenceValue);
                if (_stop) break;
                if (warm) continue;
                var sorted = frameTimes.OrderBy(x => x).ToArray();
                double p99 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] : 0;
                double sum = frameTimes.Sum() / 1000;
                passes.Add(new RtPass(sum > 0 ? frameTimes.Count / sum : 0, p99 > 0 ? 1000 / p99 : 0, frameTimes.Count));
            }

            if (_stop) { Cancelled = true; return null; }
            var result = new GpuRtResult(passes, Width, Height, Preset, adapterName, 0);
            double grays = result.MedianFps * Width * Height * RtScene.RaysPerPixel(Preset) / 1e9;
            return result with { GigaRaysPerSec = grays };
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            try { if (queue != null && fence != null) { queue.Signal(fence, ++fenceValue); WaitGpu(fenceValue); } } catch { }
            for (int i = keep.Count - 1; i >= 0; i--) try { keep[i].Dispose(); } catch { }
            fenceEvent.Dispose();
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            UnregisterClassW(cls, hInst);
            Thread.CurrentThread.Priority = oldPriority;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_SYSCOMMAND = 0x0112;
        if (msg == WM_SYSCOMMAND && ((long)wParam & 0xFFF0) == 0xF100) return IntPtr.Zero;
        if (msg == WM_CLOSE || (msg == WM_KEYDOWN && wParam == (IntPtr)0x1B)) { _stop = true; return IntPtr.Zero; }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ Win32

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string cls, IntPtr hInst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRect(ref RECT r, uint style, bool menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hWnd, string text);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr hInst, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
