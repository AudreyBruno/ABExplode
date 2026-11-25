using System.Reflection;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace ABExplode.Vulkan;

/// <summary>Imagem na GPU com a memória e a view (render target, profundidade, resolve).</summary>
internal sealed class GpuImage
{
    public VkImage Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Format Format;
}

/// <summary>Buffer na GPU com a memória; Mapped aponta para a memória quando ela é visível pela CPU.</summary>
internal sealed unsafe class GpuBuffer
{
    public VkBuffer Buffer;
    public DeviceMemory Memory;
    public void* Mapped;
    public ulong Size;
}

/// <summary>
/// Base Vulkan dos benchmarks: instância, superfície da janela (GLFW), escolha da GPU (pelo nome, senão a dedicada),
/// dispositivo, fila, swapchain e utilidades de buffer, imagem e shader. Tudo é destruído em <see cref="Dispose"/>.
/// </summary>
internal sealed unsafe class VkGpu : IDisposable
{
    public readonly Vk Vk = Vk.GetApi();
    public Instance Instance;
    public PhysicalDevice Physical;
    public Device Device;
    public Queue Queue;
    public uint QueueFamily;
    public SurfaceKHR Surface;
    public KhrSurface KhrSurface = null!;
    public KhrSwapchain KhrSwapchain = null!;
    public string AdapterName = "";
    public PhysicalDeviceMemoryProperties MemProps;
    public PhysicalDeviceLimits Limits;
    /// <summary>Bits válidos dos timestamps da fila (0 = a fila não mede tempo).</summary>
    public uint TimestampBits;

    // Ray tracing (só com rayQuery: true)
    public KhrAccelerationStructure? KhrAs;
    public ulong ScratchAlignment = 256;

    // Swapchain
    public SwapchainKHR Swapchain;
    public VkImage[] SwapImages = Array.Empty<VkImage>();
    public Format SwapFormat;
    public Extent2D Extent;

    public CommandPool Pool;

    public static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"Vulkan: {what} falhou ({r}).");
    }

    /// <summary>Extensões de ray tracing com raios inline (VK_KHR_ray_query), as mesmas do DXR 1.1.</summary>
    internal static readonly string[] RayQueryExtensions =
        { KhrAccelerationStructure.ExtensionName, "VK_KHR_ray_query", "VK_KHR_deferred_host_operations" };

    /// <param name="rayQuery">Pede ray tracing por hardware: só aceita GPU com <see cref="RayQueryExtensions"/>.</param>
    public VkGpu(IWindow window, string adapterName, bool rayQuery = false)
    {
        if (window.VkSurface == null) throw new InvalidOperationException("Esta janela não tem suporte a Vulkan (driver de vídeo sem Vulkan?).");

        // ---- Instância ----
        var appName = (byte*)SilkMarshal.StringToPtr("ABExplode");
        var app = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo, PApplicationName = appName, ApplicationVersion = 1,
            PEngineName = appName, EngineVersion = 1, ApiVersion = rayQuery ? Vk.Version12 : Vk.Version11,
        };
        byte** ext = window.VkSurface.GetRequiredExtensions(out uint extCount);
        var ici = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app,
            EnabledExtensionCount = extCount, PpEnabledExtensionNames = ext,
        };
        Check(Vk.CreateInstance(in ici, null, out Instance), "criar a instância");
        SilkMarshal.Free((nint)appName);
        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface)) throw new InvalidOperationException("Vulkan sem VK_KHR_surface.");
        Surface = window.VkSurface.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();

        // ---- GPU: a do nome pedido, senão a dedicada, senão a primeira que apresenta na janela ----
        uint n = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref n, null);
        var devices = new PhysicalDevice[n];
        fixed (PhysicalDevice* p = devices) Vk.EnumeratePhysicalDevices(Instance, ref n, p);
        var candidates = new List<(PhysicalDevice Dev, string Name, PhysicalDeviceType Type, uint Family)>();
        foreach (var d in devices)
        {
            Vk.GetPhysicalDeviceProperties(d, out var props);
            string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "GPU";
            if (props.DeviceType == PhysicalDeviceType.Cpu && !AllowSoftware) continue; // llvmpipe: renderizador por software
            if (rayQuery && !HasRayQuery(Vk, d)) continue;
            if (FindQueueFamily(d) is uint fam) candidates.Add((d, name, props.DeviceType, fam));
        }
        if (candidates.Count == 0)
            throw rayQuery ? new NotSupportedException(NoRayQuery) : new InvalidOperationException("Nenhuma GPU com Vulkan que consiga desenhar nesta janela.");
        static bool Same(string a, string b) => a.Length > 0 && b.Length > 0 &&
            (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase) ||
             Strip(a).Contains(Strip(b), StringComparison.OrdinalIgnoreCase) || Strip(b).Contains(Strip(a), StringComparison.OrdinalIgnoreCase));
        // "AMD Radeon RX 7900 XTX (RADV NAVI31)" e "NVIDIA GeForce RTX 4070" contra os nomes do sistema
        static string Strip(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s*\([^)]*\)|\(R\)|\(TM\)", "").Trim();
        var pick = candidates.FirstOrDefault(c => Same(c.Name, adapterName));
        if (pick.Name == null) pick = candidates.OrderByDescending(c => c.Type == PhysicalDeviceType.DiscreteGpu).First();
        Physical = pick.Dev;
        AdapterName = pick.Name;
        QueueFamily = pick.Family;
        Vk.GetPhysicalDeviceMemoryProperties(Physical, out MemProps);
        Vk.GetPhysicalDeviceProperties(Physical, out var chosen);
        Limits = chosen.Limits;
        uint nf = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(Physical, ref nf, null);
        var families = new QueueFamilyProperties[nf];
        fixed (QueueFamilyProperties* p = families) Vk.GetPhysicalDeviceQueueFamilyProperties(Physical, ref nf, p);
        TimestampBits = families[QueueFamily].TimestampValidBits;
        if (rayQuery)
        {
            var asProps = new PhysicalDeviceAccelerationStructurePropertiesKHR { SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr };
            var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &asProps };
            Vk.GetPhysicalDeviceProperties2(Physical, &props2);
            ScratchAlignment = Math.Max(1u, asProps.MinAccelerationStructureScratchOffsetAlignment);
        }

        // ---- Dispositivo e fila ----
        float prio = 1f;
        var qci = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = QueueFamily, QueueCount = 1, PQueuePriorities = &prio };
        var extNames = new[] { KhrSwapchain.ExtensionName }.Concat(rayQuery ? RayQueryExtensions : Array.Empty<string>()).ToArray();
        var devExt = (byte**)SilkMarshal.StringArrayToPtr(extNames);
        var features = new PhysicalDeviceFeatures();
        // Ray tracing: estruturas de aceleração, raios inline, endereço de buffer (a GPU lê a geometria pelo endereço) e
        // layout escalar (os buffers do shader têm o formato das structs do C#)
        var rq = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr, RayQuery = true };
        var asf = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr, PNext = &rq, AccelerationStructure = true };
        var v12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &asf, BufferDeviceAddress = true, ScalarBlockLayout = true };
        var dci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo, PNext = rayQuery ? &v12 : null, QueueCreateInfoCount = 1, PQueueCreateInfos = &qci,
            EnabledExtensionCount = (uint)extNames.Length, PpEnabledExtensionNames = devExt, PEnabledFeatures = &features,
        };
        Check(Vk.CreateDevice(Physical, in dci, null, out Device), "criar o dispositivo");
        SilkMarshal.Free((nint)devExt);
        Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue);
        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain)) throw new InvalidOperationException("Vulkan sem VK_KHR_swapchain.");
        if (rayQuery)
        {
            if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrAccelerationStructure kas)) throw new NotSupportedException(NoRayQuery);
            KhrAs = kas;
        }

        var pci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(Vk.CreateCommandPool(Device, in pci, null, out Pool), "criar o command pool");
    }

    /// <summary>Só para teste: ABEXPLODE_VK_SOFTWARE=1 aceita o Vulkan por software (lavapipe), para conferir o ray tracing sem placa com RT.</summary>
    internal static bool AllowSoftware => Environment.GetEnvironmentVariable("ABEXPLODE_VK_SOFTWARE") == "1";

    internal const string NoRayQuery =
        "Esta placa não tem ray tracing por hardware no Vulkan (VK_KHR_ray_query). Precisa de RTX 20 ou mais nova, Radeon RX 6000 " +
        "ou mais nova, ou Intel Arc, com driver atualizado (no Linux: driver NVIDIA proprietário ou Mesa 22 ou mais novo).";

    /// <summary>A GPU tem Vulkan 1.2, as extensões de ray query e os recursos ligados?</summary>
    internal static bool HasRayQuery(Vk vk, PhysicalDevice d)
    {
        vk.GetPhysicalDeviceProperties(d, out var props);
        if (props.ApiVersion < Vk.Version12) return false;
        uint n = 0;
        vk.EnumerateDeviceExtensionProperties(d, (byte*)null, ref n, null);
        var exts = new ExtensionProperties[n];
        fixed (ExtensionProperties* p = exts) vk.EnumerateDeviceExtensionProperties(d, (byte*)null, ref n, p);
        var names = new HashSet<string>();
        foreach (var e in exts) names.Add(SilkMarshal.PtrToString((nint)e.ExtensionName) ?? "");
        if (!RayQueryExtensions.All(names.Contains)) return false;
        var rq = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        var asf = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr, PNext = &rq };
        var v12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &asf };
        var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &v12 };
        vk.GetPhysicalDeviceFeatures2(d, &f2);
        return rq.RayQuery && asf.AccelerationStructure && v12.BufferDeviceAddress && v12.ScalarBlockLayout;
    }

    private uint? FindQueueFamily(PhysicalDevice d)
    {
        uint n = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(d, ref n, null);
        var fams = new QueueFamilyProperties[n];
        fixed (QueueFamilyProperties* p = fams) Vk.GetPhysicalDeviceQueueFamilyProperties(d, ref n, p);
        for (uint i = 0; i < n; i++)
        {
            if ((fams[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(d, i, Surface, out var ok);
            if (ok) return i;
        }
        return null;
    }

    // ------------------------------------------------------------------ swapchain

    /// <summary>Cria (ou recria, depois de redimensionar) a swapchain. Sem vsync quando possível: o benchmark mede a GPU, não o monitor.</summary>
    public void CreateSwapchain(int width, int height)
    {
        Vk.DeviceWaitIdle(Device);
        KhrSurface.GetPhysicalDeviceSurfaceCapabilities(Physical, Surface, out var caps);
        uint n = 0;
        KhrSurface.GetPhysicalDeviceSurfaceFormats(Physical, Surface, ref n, null);
        var formats = new SurfaceFormatKHR[n];
        fixed (SurfaceFormatKHR* p = formats) KhrSurface.GetPhysicalDeviceSurfaceFormats(Physical, Surface, ref n, p);
        var fmt = formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm);
        if (fmt.Format == Format.Undefined) fmt = formats[0];

        n = 0;
        KhrSurface.GetPhysicalDeviceSurfacePresentModes(Physical, Surface, ref n, null);
        var modes = new PresentModeKHR[n];
        fixed (PresentModeKHR* p = modes) KhrSurface.GetPhysicalDeviceSurfacePresentModes(Physical, Surface, ref n, p);
        var mode = modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr
                 : modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;

        Extent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent
            : new Extent2D(Math.Clamp((uint)width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                           Math.Clamp((uint)height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        uint count = Math.Max(caps.MinImageCount, 2);
        if (caps.MaxImageCount > 0) count = Math.Min(count, caps.MaxImageCount);

        var old = Swapchain;
        var sci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr, Surface = Surface, MinImageCount = count, ImageFormat = fmt.Format,
            ImageColorSpace = fmt.ColorSpace, ImageExtent = Extent, ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit, ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform, CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr, PresentMode = mode,
            Clipped = true, OldSwapchain = old,
        };
        Check(KhrSwapchain.CreateSwapchain(Device, in sci, null, out Swapchain), "criar a swapchain");
        if (old.Handle != 0) KhrSwapchain.DestroySwapchain(Device, old, null);
        SwapFormat = fmt.Format;
        n = 0;
        KhrSwapchain.GetSwapchainImages(Device, Swapchain, ref n, null);
        SwapImages = new VkImage[n];
        fixed (VkImage* p = SwapImages) KhrSwapchain.GetSwapchainImages(Device, Swapchain, ref n, p);
    }

    // ------------------------------------------------------------------ memória, buffers e imagens

    private uint MemoryType(uint bits, MemoryPropertyFlags want)
    {
        for (int i = 0; i < MemProps.MemoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (MemProps.MemoryTypes[i].PropertyFlags & want) == want) return (uint)i;
        throw new InvalidOperationException("Vulkan: nenhum tipo de memória adequado.");
    }

    public GpuBuffer CreateBuffer(ulong size, BufferUsageFlags usage, bool hostVisible)
    {
        var b = new GpuBuffer { Size = size };
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(Vk.CreateBuffer(Device, in bci, null, out b.Buffer), "criar buffer");
        Vk.GetBufferMemoryRequirements(Device, b.Buffer, out var req);
        var want = hostVisible ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit : MemoryPropertyFlags.DeviceLocalBit;
        // Buffer lido pelo endereço (geometria e estruturas do ray tracing): a memória precisa ter endereço de GPU
        var flags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = (MemoryAllocateFlags)2 }; // VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT
        var mai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, want),
            PNext = (usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0 ? &flags : null,
        };
        Check(Vk.AllocateMemory(Device, in mai, null, out b.Memory), "alocar memória");
        Vk.BindBufferMemory(Device, b.Buffer, b.Memory, 0);
        if (hostVisible)
        {
            void* ptr;
            Vk.MapMemory(Device, b.Memory, 0, size, 0, &ptr);
            b.Mapped = ptr;
        }
        return b;
    }

    /// <summary>Endereço do buffer na GPU (precisa de BufferUsageFlags.ShaderDeviceAddressBit).</summary>
    public ulong Address(GpuBuffer b)
    {
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = b.Buffer };
        return Vk.GetBufferDeviceAddress(Device, in info);
    }

    /// <summary>Buffer só da GPU preenchido com os dados (cópia por um buffer temporário visível pela CPU).</summary>
    public GpuBuffer UploadBuffer<T>(T[] data, BufferUsageFlags usage) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        var staging = CreateBuffer(size, BufferUsageFlags.TransferSrcBit, hostVisible: true);
        fixed (T* p = data) System.Buffer.MemoryCopy(p, staging.Mapped, (long)size, (long)size);
        var dst = CreateBuffer(size, usage | BufferUsageFlags.TransferDstBit, hostVisible: false);
        OneShot(cmd =>
        {
            var region = new BufferCopy(0, 0, size);
            Vk.CmdCopyBuffer(cmd, staging.Buffer, dst.Buffer, 1, in region);
        });
        Destroy(staging);
        return dst;
    }

    public GpuImage CreateImage(uint w, uint h, Format format, ImageUsageFlags usage, SampleCountFlags samples, ImageAspectFlags aspect)
    {
        var img = new GpuImage { Format = format };
        var ici = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(w, h, 1),
            MipLevels = 1, ArrayLayers = 1, Samples = samples, Tiling = ImageTiling.Optimal, Usage = usage,
            SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        Check(Vk.CreateImage(Device, in ici, null, out img.Image), "criar imagem");
        Vk.GetImageMemoryRequirements(Device, img.Image, out var req);
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        Check(Vk.AllocateMemory(Device, in mai, null, out img.Memory), "alocar memória da imagem");
        Vk.BindImageMemory(Device, img.Image, img.Memory, 0);
        var vci = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = img.Image, ViewType = ImageViewType.Type2D, Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        Check(Vk.CreateImageView(Device, in vci, null, out img.View), "criar a view da imagem");
        return img;
    }

    /// <summary>Maior MSAA até o pedido que a GPU aceita para cor e profundidade.</summary>
    public SampleCountFlags Samples(int wanted)
    {
        var ok = Limits.FramebufferColorSampleCounts & Limits.FramebufferDepthSampleCounts;
        for (int s = wanted; s > 1; s /= 2)
            if ((ok & (SampleCountFlags)s) != 0) return (SampleCountFlags)s;
        return SampleCountFlags.Count1Bit;
    }

    public ShaderModule LoadShader(string resourceName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shaders/" + resourceName)
                      ?? throw new InvalidOperationException($"Shader {resourceName} não embutido.");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        fixed (byte* p = bytes)
        {
            var smci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)bytes.Length, PCode = (uint*)p };
            Check(Vk.CreateShaderModule(Device, in smci, null, out var m), "carregar o shader " + resourceName);
            return m;
        }
    }

    /// <summary>Grava e executa um comando avulso, esperando terminar (cópias de inicialização).</summary>
    public void OneShot(Action<CommandBuffer> record)
    {
        var cmd = BeginOnce();
        record(cmd);
        SubmitOnce(cmd);
    }

    /// <summary>Começa um comando avulso; <see cref="SubmitOnce"/> executa e espera.</summary>
    public CommandBuffer BeginOnce()
    {
        var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = Pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Vk.AllocateCommandBuffers(Device, in ai, out var cmd);
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Vk.BeginCommandBuffer(cmd, in bi);
        return cmd;
    }

    public void SubmitOnce(CommandBuffer cmd)
    {
        Vk.EndCommandBuffer(cmd);
        var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Vk.QueueSubmit(Queue, 1, in si, default);
        Vk.QueueWaitIdle(Queue);
        Vk.FreeCommandBuffers(Device, Pool, 1, in cmd);
    }

    /// <summary>Barreira de layout de imagem (para a cópia para a swapchain e a apresentação).</summary>
    public void Barrier(CommandBuffer cmd, VkImage image, ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess,
                        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var b = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = from, NewLayout = to, Image = image,
            SrcAccessMask = srcAccess, DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in b);
    }

    public void Destroy(GpuBuffer? b)
    {
        if (b == null) return;
        if (b.Mapped != null) Vk.UnmapMemory(Device, b.Memory);
        Vk.DestroyBuffer(Device, b.Buffer, null);
        Vk.FreeMemory(Device, b.Memory, null);
    }

    public void Destroy(GpuImage? i)
    {
        if (i == null) return;
        Vk.DestroyImageView(Device, i.View, null);
        Vk.DestroyImage(Device, i.Image, null);
        Vk.FreeMemory(Device, i.Memory, null);
    }

    public void Dispose()
    {
        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            if (Pool.Handle != 0) Vk.DestroyCommandPool(Device, Pool, null);
            if (Swapchain.Handle != 0) KhrSwapchain.DestroySwapchain(Device, Swapchain, null);
            Vk.DestroyDevice(Device, null);
        }
        if (Surface.Handle != 0) KhrSurface?.DestroySurface(Instance, Surface, null);
        if (Instance.Handle != 0) Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }
}
