using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ABExplode.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace ABExplode.Vulkan;

/// <summary>
/// RESET BENCH · Ray tracing em Vulkan (VK_KHR_ray_query), igual ao DXR 1.1 do Windows: mesma cena (<see cref="RtScene"/>),
/// mesmo path tracer em HLSL com raios inline num compute shader. A estrutura de aceleração do topo (TLAS) é refeita a cada
/// frame com objetos girando. O tempo de frame vem dos timestamps da GPU. Aquecimento sem medir, depois as passadas; nota
/// pela mediana. As notas de Vulkan e DXR não são comparáveis entre si.
/// </summary>
public sealed unsafe class GpuRtBench
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
    public void Cancel() => _stop = true;

    /// <summary>Verifica se alguma GPU tem ray tracing por hardware no Vulkan. Retorna null se tiver, senão o motivo.</summary>
    public static string? CheckSupport(string adapterName)
    {
        try
        {
            using var vk = Vk.GetApi();
            var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version12 };
            var ici = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
            VkGpu.Check(vk.CreateInstance(in ici, null, out var instance), "criar a instância");
            try
            {
                uint n = 0;
                vk.EnumeratePhysicalDevices(instance, ref n, null);
                var devices = new PhysicalDevice[n];
                fixed (PhysicalDevice* p = devices) vk.EnumeratePhysicalDevices(instance, ref n, p);
                foreach (var d in devices)
                {
                    vk.GetPhysicalDeviceProperties(d, out var props);
                    if ((props.DeviceType != PhysicalDeviceType.Cpu || VkGpu.AllowSoftware) && VkGpu.HasRayQuery(vk, d)) return null;
                }
                return VkGpu.NoRayQuery;
            }
            finally { vk.DestroyInstance(instance, null); }
        }
        catch (Exception ex) { return $"Vulkan indisponível: {ex.Message}"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CamPos; public float Time;
        public Vector3 SunDir; public uint FrameIndex;
        public uint Width, Height, Spp, Bounces;
    }

    /// <summary>VkAccelerationStructureInstanceKHR (64 bytes, o mesmo formato do DXR): matriz 3x4, id(24)+máscara(8),
    /// deslocamento(24)+flags(8), endereço da BLAS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceDesc
    {
        public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
        public uint IdMask, ContribFlags;
        public ulong Blas;
    }

    private const int InFlight = 4;

    public GpuRtResult? Run()
    {
        var oldPriority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        IWindow? window = null;
        VkGpu? g = null;
        Tracer? tracer = null;
        try
        {
            var screen = Silk.NET.Windowing.Monitor.GetMainMonitor(null)?.VideoMode.Resolution ?? new Vector2D<int>(1920, 1080);
            double scale = Math.Min(1.0, Math.Min(screen.X * 0.85 / Width, screen.Y * 0.85 / Height));
            var opts = WindowOptions.DefaultVulkan with
            {
                Size = new Vector2D<int>((int)(Width * scale), (int)(Height * scale)), Title = "RESET BENCH · Ray tracing (Vulkan)", VSync = false,
            };
            window = Window.Create(opts);
            window.Initialize();
            window.Closing += () => _stop = true;
            foreach (var kb in window.CreateInput().Keyboards)
                kb.KeyDown += (_, key, _) => { if (key == Key.Escape) _stop = true; };

            g = new VkGpu(window, AdapterName, rayQuery: true);
            var fb = window.FramebufferSize;
            g.CreateSwapchain(fb.X, fb.Y);
            tracer = new Tracer(g, Width, Height);

            var passes = new List<RtPass>();
            int totalPhases = Passes + (Warmup > TimeSpan.Zero ? 1 : 0);
            for (int phase = 0; phase < totalPhases && !_stop; phase++)
            {
                bool warm = Warmup > TimeSpan.Zero && phase == 0;
                var duration = warm ? Warmup : PassDuration;
                Phase = warm ? "Aquecendo" : $"Passada {phase - (Warmup > TimeSpan.Zero ? 1 : 0) + 1} de {Passes}";
                var frameTimes = tracer.Loop(this, window, duration, phase, totalPhases);
                if (_stop) break;
                if (warm) continue;
                var sorted = frameTimes.OrderBy(x => x).ToArray();
                double p99 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] : 0;
                double sum = frameTimes.Sum() / 1000;
                passes.Add(new RtPass(sum > 0 ? frameTimes.Count / sum : 0, p99 > 0 ? 1000 / p99 : 0, frameTimes.Count));
            }

            if (_stop) { Cancelled = true; return null; }
            var result = new GpuRtResult(passes, Width, Height, Preset, g.AdapterName, 0);
            return result with { GigaRaysPerSec = result.MedianFps * Width * Height * RtScene.RaysPerPixel(Preset) / 1e9 };
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            try { tracer?.Destroy(); } catch { }
            try { g?.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
            Thread.CurrentThread.Priority = oldPriority;
        }
    }

    /// <summary>Recursos do Vulkan: geometria, estruturas de aceleração, pipeline de compute e os frames na fila.</summary>
    private sealed class Tracer
    {
        private readonly VkGpu _g;
        private readonly Vk _vk;
        private readonly KhrAccelerationStructure _as;
        private readonly uint _w, _h;
        private readonly List<GpuBuffer> _buffers = new();
        private readonly List<AccelerationStructureKHR> _structures = new();

        private GpuBuffer _vb = null!, _ib = null!, _meshInfo = null!, _mats = null!;
        private AccelerationStructureKHR[] _blas = null!;
        private ulong[] _blasAddr = null!;
        private List<RtObject> _objects = null!;
        private AccelerationStructureKHR _tlas;
        private ulong _tlasScratch;
        private AccelerationStructureBuildSizesInfoKHR _tlasSizes;

        private GpuImage _output = null!;
        private DescriptorSetLayout _setLayout;
        private PipelineLayout _layout;
        private DescriptorPool _descPool;
        private Pipeline _pipeline;
        private QueryPool _timestamps;
        private uint _frameIndex;

        private readonly CommandBuffer[] _cmds = new CommandBuffer[InFlight];
        private readonly Fence[] _fences = new Fence[InFlight];
        private readonly GpuBuffer[] _ubos = new GpuBuffer[InFlight], _instances = new GpuBuffer[InFlight];
        private readonly DescriptorSet[] _sets = new DescriptorSet[InFlight];
        private readonly VkSemaphore[] _acquired = new VkSemaphore[InFlight], _done = new VkSemaphore[InFlight];

        public Tracer(VkGpu g, int width, int height)
        {
            _g = g; _vk = g.Vk; _as = g.KhrAs!; _w = (uint)width; _h = (uint)height;
            CreateGeometry();
            CreateTlas();
            CreateOutput();
            CreatePipeline();
            CreateFrames();
        }

        private GpuBuffer Keep(GpuBuffer b) { _buffers.Add(b); return b; }

        private ulong Aligned(ulong address) => (address + _g.ScratchAlignment - 1) / _g.ScratchAlignment * _g.ScratchAlignment;

        private const BufferUsageFlags BuildInput = BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;

        /// <summary>Cria a estrutura de aceleração no tamanho pedido (num buffer próprio).</summary>
        private AccelerationStructureKHR CreateStructure(AccelerationStructureTypeKHR type, ulong size)
        {
            var buf = Keep(_g.CreateBuffer(size, BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false));
            var ci = new AccelerationStructureCreateInfoKHR { SType = StructureType.AccelerationStructureCreateInfoKhr, Buffer = buf.Buffer, Size = size, Type = type };
            VkGpu.Check(_as.CreateAccelerationStructure(_g.Device, in ci, null, out var s), "criar a estrutura de aceleração");
            _structures.Add(s);
            return s;
        }

        private static void AsBarrier(Vk vk, CommandBuffer cmd)
        {
            // Barreira ampla: cada etapa (montar estrutura, traçar, copiar) espera a anterior terminar de escrever
            var mb = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit, DstAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit,
            };
            vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, in mb, 0, null, 0, null);
        }

        // ---------------- geometria e BLAS ----------------

        private void CreateGeometry()
        {
            var mb = RtScene.BuildMeshes();
            _vb = Keep(_g.UploadBuffer(mb.V.ToArray(), BufferUsageFlags.StorageBufferBit | BuildInput));
            _ib = Keep(_g.UploadBuffer(mb.I.ToArray(), BufferUsageFlags.StorageBufferBit | BuildInput));
            _meshInfo = Keep(_g.UploadBuffer(mb.Meshes.Select(m => new RtMeshInfo { IndexOffset = m.IndexOffset, VertexOffset = m.VertexOffset }).ToArray(),
                                             BufferUsageFlags.StorageBufferBit));
            _objects = RtScene.BuildObjects();
            _mats = Keep(_g.UploadBuffer(_objects.Select(o => o.Mat).ToArray(), BufferUsageFlags.StorageBufferBit));

            // Uma BLAS por malha, montada uma vez
            ulong vbAddr = _g.Address(_vb), ibAddr = _g.Address(_ib);
            _blas = new AccelerationStructureKHR[mb.Meshes.Count];
            _blasAddr = new ulong[mb.Meshes.Count];
            var scratches = new List<GpuBuffer>();
            var cmd = _g.BeginOnce();
            for (int m = 0; m < mb.Meshes.Count; m++)
            {
                var (io, ic, vo, vc) = mb.Meshes[m];
                var tri = new AccelerationStructureGeometryTrianglesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr, VertexFormat = Format.R32G32B32Sfloat,
                    VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vbAddr + vo * (ulong)sizeof(RtVertex) },
                    VertexStride = (ulong)sizeof(RtVertex), MaxVertex = vc - 1, IndexType = IndexType.Uint32,
                    IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = ibAddr + io * 4UL },
                };
                var geo = new AccelerationStructureGeometryKHR
                {
                    SType = StructureType.AccelerationStructureGeometryKhr, GeometryType = GeometryTypeKHR.TrianglesKhr,
                    Geometry = new AccelerationStructureGeometryDataKHR { Triangles = tri }, Flags = GeometryFlagsKHR.OpaqueBitKhr,
                };
                var build = new AccelerationStructureBuildGeometryInfoKHR
                {
                    SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                    Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                    GeometryCount = 1, PGeometries = &geo,
                };
                uint prims = ic / 3;
                var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
                _as.GetAccelerationStructureBuildSizes(_g.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, &prims, &sizes);
                _blas[m] = CreateStructure(AccelerationStructureTypeKHR.BottomLevelKhr, sizes.AccelerationStructureSize);
                var scratch = _g.CreateBuffer(sizes.BuildScratchSize + _g.ScratchAlignment, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false);
                scratches.Add(scratch);
                build.DstAccelerationStructure = _blas[m];
                build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = Aligned(_g.Address(scratch)) };
                var range = new AccelerationStructureBuildRangeInfoKHR(prims, 0, 0, 0);
                var pRange = &range;
                _as.CmdBuildAccelerationStructures(cmd, 1, &build, &pRange);
            }
            _g.SubmitOnce(cmd);
            foreach (var s in scratches) _g.Destroy(s);
            for (int m = 0; m < _blas.Length; m++)
            {
                var ai = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = _blas[m] };
                _blasAddr[m] = _as.GetAccelerationStructureDeviceAddress(_g.Device, in ai);
            }
        }

        // ---------------- TLAS: refeita a cada frame (objetos girando) ----------------

        private AccelerationStructureGeometryKHR TlasGeometry(ulong instances) => new()
        {
            SType = StructureType.AccelerationStructureGeometryKhr, GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = new AccelerationStructureGeometryInstancesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr, ArrayOfPointers = false,
                    Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances },
                },
            },
        };

        private void CreateTlas()
        {
            for (int i = 0; i < InFlight; i++)
                _instances[i] = Keep(_g.CreateBuffer((ulong)(_objects.Count * sizeof(InstanceDesc)), BuildInput, hostVisible: true));
            var geo = TlasGeometry(0);
            var build = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastBuildBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = 1, PGeometries = &geo,
            };
            uint count = (uint)_objects.Count;
            _tlasSizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
            fixed (AccelerationStructureBuildSizesInfoKHR* s = &_tlasSizes)
                _as.GetAccelerationStructureBuildSizes(_g.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, &count, s);
            _tlas = CreateStructure(AccelerationStructureTypeKHR.TopLevelKhr, _tlasSizes.AccelerationStructureSize);
            var scratch = Keep(_g.CreateBuffer(_tlasSizes.BuildScratchSize + _g.ScratchAlignment, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, hostVisible: false));
            _tlasScratch = Aligned(_g.Address(scratch));
        }

        private void BuildTlas(CommandBuffer cmd, int slot)
        {
            var geo = TlasGeometry(_g.Address(_instances[slot]));
            var build = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastBuildBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = 1, PGeometries = &geo, DstAccelerationStructure = _tlas,
                ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = _tlasScratch },
            };
            var range = new AccelerationStructureBuildRangeInfoKHR((uint)_objects.Count, 0, 0, 0);
            var pRange = &range;
            _as.CmdBuildAccelerationStructures(cmd, 1, &build, &pRange);
        }

        // ---------------- saída, pipeline e frames ----------------

        private void CreateOutput()
        {
            _output = _g.CreateImage(_w, _h, Format.R8G8B8A8Unorm, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit,
                                     SampleCountFlags.Count1Bit, ImageAspectFlags.ColorBit);
            // Fica sempre em General: o shader escreve e a cópia para a janela lê dali
            var cmd = _g.BeginOnce();
            _g.Barrier(cmd, _output.Image, ImageLayout.Undefined, ImageLayout.General, 0, AccessFlags.ShaderWriteBit,
                       PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ComputeShaderBit);
            _g.SubmitOnce(cmd);
        }

        private void CreatePipeline()
        {
            var b = stackalloc DescriptorSetLayoutBinding[7];
            b[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit);
            b[1] = new DescriptorSetLayoutBinding(1, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit);
            for (uint i = 2; i <= 5; i++) b[i] = new DescriptorSetLayoutBinding(i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            b[6] = new DescriptorSetLayoutBinding(6, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
            var slci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 7, PBindings = b };
            VkGpu.Check(_vk.CreateDescriptorSetLayout(_g.Device, in slci, null, out _setLayout), "criar o layout de descritores");
            var sl = _setLayout;
            var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &sl };
            VkGpu.Check(_vk.CreatePipelineLayout(_g.Device, in plci, null, out _layout), "criar o layout do pipeline");

            var sizes = stackalloc DescriptorPoolSize[4];
            sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, InFlight);
            sizes[1] = new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, InFlight);
            sizes[2] = new DescriptorPoolSize(DescriptorType.StorageBuffer, InFlight * 4);
            sizes[3] = new DescriptorPoolSize(DescriptorType.StorageImage, InFlight);
            var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = InFlight, PoolSizeCount = 4, PPoolSizes = sizes };
            VkGpu.Check(_vk.CreateDescriptorPool(_g.Device, in dpci, null, out _descPool), "criar o pool de descritores");

            var module = _g.LoadShader("rt.Main.spv");
            var name = (byte*)SilkMarshal.StringToPtr("Main");
            try
            {
                var cpci = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo, Layout = _layout,
                    Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = name },
                };
                VkGpu.Check(_vk.CreateComputePipelines(_g.Device, default, 1, in cpci, null, out _pipeline), "criar o pipeline de ray tracing");
            }
            finally
            {
                SilkMarshal.Free((nint)name);
                _vk.DestroyShaderModule(_g.Device, module, null);
            }

            if (_g.TimestampBits > 0)
            {
                var qpci = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = InFlight };
                VkGpu.Check(_vk.CreateQueryPool(_g.Device, in qpci, null, out _timestamps), "criar os timestamps");
            }
        }

        private void CreateFrames()
        {
            var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _g.Pool, Level = CommandBufferLevel.Primary, CommandBufferCount = InFlight };
            fixed (CommandBuffer* p = _cmds) VkGpu.Check(_vk.AllocateCommandBuffers(_g.Device, in ai, p), "alocar os command buffers");
            var w = stackalloc WriteDescriptorSet[7];
            var bufs = stackalloc DescriptorBufferInfo[4];
            for (int i = 0; i < InFlight; i++)
            {
                _ubos[i] = Keep(_g.CreateBuffer((ulong)sizeof(FrameCB), BufferUsageFlags.UniformBufferBit, hostVisible: true));
                var sl = _setLayout;
                var dsai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descPool, DescriptorSetCount = 1, PSetLayouts = &sl };
                VkGpu.Check(_vk.AllocateDescriptorSets(_g.Device, in dsai, out _sets[i]), "alocar descritores");

                var ubo = new DescriptorBufferInfo(_ubos[i].Buffer, 0, (ulong)sizeof(FrameCB));
                var tlas = _tlas;
                var asInfo = new WriteDescriptorSetAccelerationStructureKHR
                {
                    SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &tlas,
                };
                bufs[0] = new DescriptorBufferInfo(_vb.Buffer, 0, Vk.WholeSize);
                bufs[1] = new DescriptorBufferInfo(_ib.Buffer, 0, Vk.WholeSize);
                bufs[2] = new DescriptorBufferInfo(_meshInfo.Buffer, 0, Vk.WholeSize);
                bufs[3] = new DescriptorBufferInfo(_mats.Buffer, 0, Vk.WholeSize);
                var img = new DescriptorImageInfo(default, _output.View, ImageLayout.General);
                WriteDescriptorSet W(uint binding, DescriptorType type) => new()
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = _sets[i], DstBinding = binding, DescriptorCount = 1, DescriptorType = type,
                };
                w[0] = W(0, DescriptorType.UniformBuffer); w[0].PBufferInfo = &ubo;
                w[1] = W(1, DescriptorType.AccelerationStructureKhr); w[1].PNext = &asInfo;
                for (int k = 0; k < 4; k++) { w[2 + k] = W((uint)(2 + k), DescriptorType.StorageBuffer); w[2 + k].PBufferInfo = &bufs[k]; }
                w[6] = W(6, DescriptorType.StorageImage); w[6].PImageInfo = &img;
                _vk.UpdateDescriptorSets(_g.Device, 7, w, 0, null);

                var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
                VkGpu.Check(_vk.CreateFence(_g.Device, in fci, null, out _fences[i]), "criar fence");
                var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
                _vk.CreateSemaphore(_g.Device, in sci, null, out _acquired[i]);
                _vk.CreateSemaphore(_g.Device, in sci, null, out _done[i]);
            }
        }

        // ---------------- loop de uma passada ----------------

        public List<double> Loop(GpuRtBench owner, IWindow window, TimeSpan duration, int phase, int totalPhases)
        {
            var frameTimes = new List<double>(20000);
            var clock = Stopwatch.StartNew();
            double lastPresent = 0, lastTitle = 0, lastDone = -1;
            int titleFrames = 0;
            long frame = 0;
            ulong lastTs = 0;
            ulong tsMask = _g.TimestampBits >= 64 ? ulong.MaxValue : (1UL << (int)_g.TimestampBits) - 1;
            double tsPeriodMs = _g.Limits.TimestampPeriod / 1e6;
            bool useTs = _timestamps.Handle != 0;
            var (spp, bounces) = RtScene.Quality(owner.Preset);
            var proj = RtScene.Projection((int)_w, (int)_h);
            var inst = new InstanceDesc[_objects.Count];
            var lastSize = window.FramebufferSize;

            while (!owner._stop && clock.Elapsed < duration)
            {
                window.DoEvents();
                if (owner._stop || window.IsClosing) break;
                var size = window.FramebufferSize;
                if (size != lastSize && size.X > 0 && size.Y > 0) { _g.CreateSwapchain(size.X, size.Y); lastSize = size; }

                int slot = (int)(frame % InFlight);
                var fence = _fences[slot];
                if (frame >= InFlight)
                {
                    if (useTs)
                    {
                        _vk.WaitForFences(_g.Device, 1, in fence, true, ulong.MaxValue);
                        // Tempo entre o fim de dois frames seguidos, no relógio da GPU
                        ulong ts = 0;
                        _vk.GetQueryPoolResults(_g.Device, _timestamps, (uint)slot, 1, 8, &ts, 8, QueryResultFlags.Result64Bit);
                        ts &= tsMask;
                        if (lastTs != 0 && ts > lastTs) frameTimes.Add((ts - lastTs) * tsPeriodMs);
                        lastTs = ts;
                    }
                    else
                    {
                        // Fila sem timestamps: o intervalo entre os frames que a GPU termina (espera ativa, como na cena de jogo)
                        while (_vk.GetFenceStatus(_g.Device, fence) == Result.NotReady && !owner._stop) Thread.SpinWait(20);
                        double done = clock.Elapsed.TotalMilliseconds;
                        if (lastDone >= 0) frameTimes.Add(done - lastDone);
                        lastDone = done;
                    }
                    titleFrames++;
                }

                // Câmera e objetos pelo tempo da passada: a mesma cena no mesmo instante em qualquer PC
                float t = (float)(clock.Elapsed.TotalSeconds / duration.TotalSeconds);
                float secs = (float)clock.Elapsed.TotalSeconds;
                var (eye, target) = RtScene.CameraAt(t);
                var vp = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * proj;
                Matrix4x4.Invert(vp, out var inv);
                *(FrameCB*)_ubos[slot].Mapped = new FrameCB
                {
                    InvViewProj = inv, CamPos = eye, Time = secs, SunDir = RtScene.SunDir, FrameIndex = _frameIndex++,
                    Width = _w, Height = _h, Spp = spp, Bounces = bounces,
                };
                for (int i = 0; i < _objects.Count; i++)
                {
                    var o = _objects[i];
                    var m = RtScene.ObjMatrix(o, secs);
                    inst[i] = new InstanceDesc
                    {
                        M00 = m.M11, M01 = m.M21, M02 = m.M31, M03 = m.M41,
                        M10 = m.M12, M11 = m.M22, M12 = m.M32, M13 = m.M42,
                        M20 = m.M13, M21 = m.M23, M22 = m.M33, M23 = m.M43,
                        IdMask = (uint)i | 0xFF000000u, ContribFlags = 0x05000000u, // CULL_DISABLE | FORCE_OPAQUE
                        Blas = _blasAddr[o.Mesh],
                    };
                }
                fixed (InstanceDesc* src = inst) System.Buffer.MemoryCopy(src, _instances[slot].Mapped, (long)inst.Length * sizeof(InstanceDesc), (long)inst.Length * sizeof(InstanceDesc));

                // Mostra na janela a ~30 Hz, sem esperar (cópia barata); o resto dos frames só é calculado
                double now = clock.Elapsed.TotalSeconds;
                uint imageIndex = 0;
                bool present = false;
                if (now - lastPresent >= 1 / 30.0)
                {
                    var r = _g.KhrSwapchain.AcquireNextImage(_g.Device, _g.Swapchain, 0, _acquired[slot], default, ref imageIndex);
                    present = r is Result.Success or Result.SuboptimalKhr;
                    if (r == Result.ErrorOutOfDateKhr && size.X > 0 && size.Y > 0) _g.CreateSwapchain(size.X, size.Y);
                }
                _vk.ResetFences(_g.Device, 1, in fence);
                Record(_cmds[slot], slot, present ? imageIndex : null);

                var cmd = _cmds[slot];
                var waitStage = PipelineStageFlags.TransferBit;
                var acquired = _acquired[slot];
                var doneSem = _done[slot];
                var si = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd,
                    WaitSemaphoreCount = present ? 1u : 0u, PWaitSemaphores = &acquired, PWaitDstStageMask = &waitStage,
                    SignalSemaphoreCount = present ? 1u : 0u, PSignalSemaphores = &doneSem,
                };
                VkGpu.Check(_vk.QueueSubmit(_g.Queue, 1, in si, fence), "enviar o frame");
                frame++;

                if (present)
                {
                    var sc = _g.Swapchain;
                    var pi = new PresentInfoKHR
                    {
                        SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &doneSem,
                        SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &imageIndex,
                    };
                    _g.KhrSwapchain.QueuePresent(_g.Queue, in pi);
                    lastPresent = now;
                }
                if (now - lastTitle >= 0.5)
                {
                    owner.CurrentFps = titleFrames / (now - lastTitle);
                    titleFrames = 0; lastTitle = now;
                    owner.Progress = (phase + now / duration.TotalSeconds) / totalPhases;
                    window.Title = $"RESET BENCH · Ray tracing (Vulkan) · {_w}x{_h} · {owner.Preset} · {owner.Phase} · {owner.CurrentFps:0} FPS";
                }
            }
            _vk.DeviceWaitIdle(_g.Device);
            return frameTimes;
        }

        private void Record(CommandBuffer cmd, int slot, uint? swapIndex)
        {
            _vk.ResetCommandBuffer(cmd, 0);
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _vk.BeginCommandBuffer(cmd, in bi);
            if (_timestamps.Handle != 0) _vk.CmdResetQueryPool(cmd, _timestamps, (uint)slot, 1);

            // A TLAS e a imagem de saída são as mesmas em todos os frames: espera o frame anterior terminar de usá-las
            AsBarrier(_vk, cmd);
            BuildTlas(cmd, slot);
            AsBarrier(_vk, cmd);

            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            var set = _sets[slot];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _layout, 0, 1, in set, 0, null);
            _vk.CmdDispatch(cmd, (_w + 7) / 8, (_h + 7) / 8, 1);
            AsBarrier(_vk, cmd);

            if (swapIndex is uint idx)
            {
                // Cópia para a janela, reduzida para caber (filtro linear)
                var swap = _g.SwapImages[idx];
                _g.Barrier(cmd, swap, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit,
                           PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
                var sub = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
                var blit = new ImageBlit { SrcSubresource = sub, DstSubresource = sub };
                blit.SrcOffsets[1] = new Offset3D((int)_w, (int)_h, 1);
                blit.DstOffsets[1] = new Offset3D((int)_g.Extent.Width, (int)_g.Extent.Height, 1);
                _vk.CmdBlitImage(cmd, _output.Image, ImageLayout.General, swap, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
                _g.Barrier(cmd, swap, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, AccessFlags.TransferWriteBit, 0,
                           PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
            }
            if (_timestamps.Handle != 0) _vk.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _timestamps, (uint)slot);
            _vk.EndCommandBuffer(cmd);
        }

        public void Destroy()
        {
            _vk.DeviceWaitIdle(_g.Device);
            for (int i = 0; i < InFlight; i++)
            {
                if (_fences[i].Handle != 0) _vk.DestroyFence(_g.Device, _fences[i], null);
                if (_acquired[i].Handle != 0) _vk.DestroySemaphore(_g.Device, _acquired[i], null);
                if (_done[i].Handle != 0) _vk.DestroySemaphore(_g.Device, _done[i], null);
            }
            if (_timestamps.Handle != 0) _vk.DestroyQueryPool(_g.Device, _timestamps, null);
            if (_pipeline.Handle != 0) _vk.DestroyPipeline(_g.Device, _pipeline, null);
            if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_g.Device, _layout, null);
            if (_descPool.Handle != 0) _vk.DestroyDescriptorPool(_g.Device, _descPool, null);
            if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_g.Device, _setLayout, null);
            foreach (var s in _structures) _as.DestroyAccelerationStructure(_g.Device, s, null);
            foreach (var b in _buffers) _g.Destroy(b);
            _g.Destroy(_output);
        }
    }
}
