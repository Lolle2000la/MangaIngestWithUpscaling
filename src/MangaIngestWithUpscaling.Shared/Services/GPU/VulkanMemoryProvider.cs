using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MangaIngestWithUpscaling.Shared.Services.GPU;

/// <summary>
/// Represents GPU memory metrics queried directly from the Vulkan graphics driver.
/// <para>
/// <see cref="DeviceIndex"/> is the accelerator index the caller asked about — the same index space
/// the rest of this class uses, not the Vulkan physical-device index that
/// <see cref="VulkanMemoryProvider.ResolveGpuIndex"/> resolves it to. The two differ whenever a
/// software rasterizer is enumerated before the GPUs.
/// </para>
/// </summary>
public record VulkanGpuMemory(
    int DeviceIndex,
    string DeviceName,
    uint VendorId,
    long TotalVramBytes,
    long BudgetBytes,
    long UsedVramBytes,
    long TotalGttBytes = 0,
    long UsedGttBytes = 0
);

/// <summary>
/// Provides zero-dependency, native P/Invoke discovery of GPU VRAM and available memory budgets
/// via standard Vulkan core and the <c>VK_EXT_memory_budget</c> extension.
/// <para>
/// Works across AMD, NVIDIA, and Intel GPUs on both Linux and Windows.
/// Dynamically probes for the Vulkan loader library (<c>libvulkan.so.1</c> / <c>vulkan-1.dll</c>)
/// and gracefully degrades if Vulkan is not present on the host environment (e.g. minimal containers).
/// </para>
/// </summary>
public static class VulkanMemoryProvider
{
    private static readonly Lock InitLock = new();
    private static bool _initAttempted;
    private static IntPtr _vulkanLib;
    private static IntPtr _instance;
    private static IntPtr[]? _physicalDevices;
    private static string[]? _deviceNames;
    private static uint[]? _vendorIds;
    private static uint[]? _deviceTypes;

    private static VkGetInstanceProcAddrDelegate? _vkGetInstanceProcAddr;
    private static VkGetPhysicalDeviceMemoryProperties2Delegate? _vkGetPhysicalDeviceMemoryProperties2;
    private static VkGetPhysicalDevicePropertiesDelegate? _vkGetPhysicalDeviceProperties;
    private static VkGetPhysicalDeviceFeatures2Delegate? _vkGetPhysicalDeviceFeatures2;

    // Vulkan constants
    private const uint VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    private const uint VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2 = 1000059000;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_PROPERTIES_2 = 1000059006;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_FLOAT16_INT8_FEATURES = 1000082000;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_16BIT_STORAGE_FEATURES = 1000083000;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_BUDGET_PROPERTIES_EXT = 1000237000;
    private const uint VK_MEMORY_HEAP_DEVICE_LOCAL_BIT = 0x00000001;
    private const uint VK_PHYSICAL_DEVICE_TYPE_CPU = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct VkMemoryType
    {
        [FieldOffset(0)]
        public uint propertyFlags;

        [FieldOffset(4)]
        public uint heapIndex;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct VkMemoryHeap
    {
        [FieldOffset(0)]
        public ulong size;

        [FieldOffset(8)]
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 520)]
    private unsafe struct VkPhysicalDeviceMemoryProperties
    {
        [FieldOffset(0)]
        public uint memoryTypeCount;

        [FieldOffset(4)]
        public fixed byte memoryTypes[32 * 8]; // 32 * VkMemoryType (8 bytes each)

        [FieldOffset(260)]
        public uint memoryHeapCount;

        [FieldOffset(264)]
        public fixed byte memoryHeaps[16 * 16]; // 16 * VkMemoryHeap (16 bytes each)
    }

    [StructLayout(LayoutKind.Explicit, Size = 536)]
    private struct VkPhysicalDeviceMemoryProperties2
    {
        [FieldOffset(0)]
        public uint sType;

        [FieldOffset(8)]
        public IntPtr pNext;

        [FieldOffset(16)]
        public VkPhysicalDeviceMemoryProperties memoryProperties;
    }

    [StructLayout(LayoutKind.Explicit, Size = 272)]
    private unsafe struct VkPhysicalDeviceMemoryBudgetPropertiesEXT
    {
        [FieldOffset(0)]
        public uint sType;

        [FieldOffset(8)]
        public IntPtr pNext;

        [FieldOffset(16)]
        public fixed ulong heapBudget[16];

        [FieldOffset(144)]
        public fixed ulong heapUsage[16];
    }

    [StructLayout(LayoutKind.Explicit, Size = 824)]
    private unsafe struct VkPhysicalDeviceProperties
    {
        [FieldOffset(0)]
        public uint apiVersion;

        [FieldOffset(4)]
        public uint driverVersion;

        [FieldOffset(8)]
        public uint vendorID;

        [FieldOffset(12)]
        public uint deviceID;

        [FieldOffset(16)]
        public uint deviceType;

        [FieldOffset(20)]
        public fixed byte deviceName[256];
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VkCreateInstanceDelegate(
        in VkInstanceCreateInfo pCreateInfo,
        IntPtr pAllocator,
        out IntPtr pInstance
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VkDestroyInstanceDelegate(IntPtr instance, IntPtr pAllocator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VkEnumeratePhysicalDevicesDelegate(
        IntPtr instance,
        ref uint pPhysicalDeviceCount,
        [Out] IntPtr[]? pPhysicalDevices
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VkGetInstanceProcAddrDelegate(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPStr)] string pName
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VkGetPhysicalDevicePropertiesDelegate(
        IntPtr physicalDevice,
        out VkPhysicalDeviceProperties pProperties
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VkGetPhysicalDeviceMemoryProperties2Delegate(
        IntPtr physicalDevice,
        ref VkPhysicalDeviceMemoryProperties2 pMemoryProperties
    );

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPhysicalDeviceFeatures
    {
        public fixed uint features[55];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceFeatures2
    {
        public uint sType;
        public IntPtr pNext;
        public VkPhysicalDeviceFeatures features;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDeviceShaderFloat16Int8Features
    {
        public uint sType;
        public IntPtr pNext;
        public uint shaderFloat16;
        public uint shaderInt8;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkPhysicalDevice16BitStorageFeatures
    {
        public uint sType;
        public IntPtr pNext;
        public uint storageBuffer16BitAccess;
        public uint uniformAndStorageBuffer16BitAccess;
        public uint storagePushConstant16;
        public uint storageInputOutput16;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VkGetPhysicalDeviceFeatures2Delegate(
        IntPtr physicalDevice,
        ref VkPhysicalDeviceFeatures2 pFeatures
    );

    /// <summary>
    /// Returns true if Vulkan is available and at least one physical device was successfully enumerated.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _instance != IntPtr.Zero
                && _physicalDevices != null
                && _physicalDevices.Length > 0;
        }
    }

    /// <summary>
    /// Gets the number of accelerators — the physical devices minus the software rasterizers, so
    /// this is what <see cref="QueryDevice"/> indexes and what an out-of-range accelerator index is
    /// compared against.
    /// </summary>
    public static int DeviceCount
    {
        get
        {
            EnsureInitialized();
            if (!IsAvailable || _physicalDevices == null || _deviceTypes == null)
            {
                return 0;
            }

            int accelerators = 0;
            for (int i = 0; i < _physicalDevices.Length; i++)
            {
                if (_deviceTypes[i] != VK_PHYSICAL_DEVICE_TYPE_CPU)
                {
                    accelerators++;
                }
            }

            return accelerators;
        }
    }

    /// <summary>
    /// Queries memory metrics for every accelerator that reported memory-budget information, indexed
    /// by accelerator index. Accelerators whose driver reported none are omitted, and
    /// <see cref="QueryDevice"/> returns null for them so the caller falls back to a source that can
    /// measure usage (DRM sysfs / nvidia-smi). Software rasterizers are never listed: they are not
    /// accelerators and are not selectable as a device.
    /// </summary>
    public static IReadOnlyList<VulkanGpuMemory> QueryAllDevices()
    {
        EnsureInitialized();
        if (!IsAvailable || _physicalDevices == null)
        {
            return Array.Empty<VulkanGpuMemory>();
        }

        var list = new List<VulkanGpuMemory>(DeviceCount);
        for (int accelerator = 0; accelerator < DeviceCount; accelerator++)
        {
            if (QueryDevice(accelerator) is VulkanGpuMemory mem)
            {
                list.Add(mem);
            }
        }

        return list;
    }

    /// <summary>
    /// Queries memory metrics (total VRAM, driver budget, used VRAM) for the specified accelerator.
    /// Returns null if Vulkan is unavailable, no accelerator exists at all, or the driver reported no
    /// memory-budget information. The last case is deliberate: a zeroed budget is not an idle card,
    /// and returning null makes <c>OnnxTiler.GetAvailableVramBudget</c> fall through to DRM sysfs /
    /// nvidia-smi instead of budgeting the card as quasi-exclusive.
    /// </summary>
    public static VulkanGpuMemory? QueryDevice(int deviceIndex = 0)
    {
        EnsureInitialized();

        int targetIndex = ResolveGpuIndex(deviceIndex);
        if (targetIndex < 0)
        {
            return null;
        }

        return QueryDeviceInternal(targetIndex, AcceleratorIndexFor(targetIndex));
    }

    /// <summary>
    /// The accelerator index of an already-resolved Vulkan physical device, so
    /// <see cref="VulkanGpuMemory.DeviceIndex"/> reports the accelerator the caller asked about rather
    /// than the physical device it resolved to. The two differ whenever a software rasterizer is
    /// enumerated before the GPUs.
    /// </summary>
    private static int AcceleratorIndexFor(int physicalIndex)
    {
        if (_deviceTypes == null)
        {
            return 0;
        }

        int accelerator = 0;
        for (int i = 0; i < physicalIndex && i < _deviceTypes.Length; i++)
        {
            if (_deviceTypes[i] != VK_PHYSICAL_DEVICE_TYPE_CPU)
            {
                accelerator++;
            }
        }

        return accelerator;
    }

    /// <summary>
    /// Maps an accelerator device index to the Vulkan physical device it refers to, or -1 when the
    /// host has no accelerator at all.
    /// <para>
    /// The two indices are not the same space. <c>vkEnumeratePhysicalDevices</c> lists every
    /// physical device the driver exposes, including the software rasterizers (lavapipe, llvmpipe),
    /// while an accelerator index counts the GPUs the inference runtime can select. Indexing the
    /// Vulkan list with an accelerator index therefore depends on whether a software device happens
    /// to sort first — with no software device installed the two agree, so the mistake is invisible
    /// exactly on the machines that are easiest to test on.
    /// </para>
    /// <para>
    /// An index past the last accelerator resolves to the first one, which is the clamp the rest of
    /// this class already applied to an out-of-range index: a misconfigured device index should keep
    /// working rather than silently falling off the list. What it must not do is land on a different
    /// accelerator than the one that was asked for.
    /// </para>
    /// </summary>
    public static int ResolveGpuIndex(int acceleratorIndex)
    {
        EnsureInitialized();
        if (!IsAvailable || _physicalDevices == null || _deviceTypes == null)
        {
            return -1;
        }

        if (acceleratorIndex < 0)
        {
            acceleratorIndex = 0;
        }

        int first = -1;
        int seen = 0;
        for (int i = 0; i < _physicalDevices.Length; i++)
        {
            if (_deviceTypes[i] == VK_PHYSICAL_DEVICE_TYPE_CPU)
            {
                continue;
            }

            if (seen == acceleratorIndex)
            {
                return i;
            }

            if (first < 0)
            {
                first = i;
            }

            seen++;
        }

        return first;
    }

    /// <summary>
    /// Checks whether the specified accelerator is a software CPU device (e.g. llvmpipe, lavapipe).
    /// </summary>
    public static bool IsCpuDevice(int acceleratorIndex = 0)
    {
        int targetIndex = ResolveGpuIndex(acceleratorIndex);
        if (targetIndex < 0)
        {
            return false;
        }

        return _deviceTypes![targetIndex] == VK_PHYSICAL_DEVICE_TYPE_CPU;
    }

    /// <summary>
    /// Checks whether the specified accelerator supports native 16-bit floating point (FP16) arithmetic.
    /// Returns false if Vulkan is unavailable, the accelerator is a software CPU rasterizer, or FP16 is not supported.
    /// </summary>
    public static bool SupportsFp16(int acceleratorIndex = 0)
    {
        int targetIndex = ResolveGpuIndex(acceleratorIndex);
        if (targetIndex < 0)
        {
            return false;
        }

        // Disqualify CPU software rasterizers
        if (IsCpuDevice(acceleratorIndex))
        {
            return false;
        }

        IntPtr dev = _physicalDevices![targetIndex];
        if (dev == IntPtr.Zero)
        {
            return false;
        }

        // A successful vkGetPhysicalDeviceFeatures2 is authoritative for all three answers: a zero
        // field means the driver said "not supported", not "could not be asked". Only a query that
        // never ran (no entry point, or the call faulted) may fall back to the vendor heuristic in
        // ResolveFp16Support. All three are read on every query rather than short-circuited: the
        // decision needs the conjunction, so an early exit would leave a flag at its default false
        // and report "no fp16" for a device that has it.
        bool featureQueryRan = false;
        bool shaderFloat16 = false;
        bool storageBuffer16BitAccess = false;
        bool uniformAndStorageBuffer16BitAccess = false;

        if (_vkGetPhysicalDeviceFeatures2 != null)
        {
            try
            {
                unsafe
                {
                    var feat16 = new VkPhysicalDeviceShaderFloat16Int8Features
                    {
                        sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_FLOAT16_INT8_FEATURES,
                        pNext = IntPtr.Zero,
                    };

                    var feat2 = new VkPhysicalDeviceFeatures2
                    {
                        sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2,
                        pNext = (IntPtr)(&feat16),
                    };

                    _vkGetPhysicalDeviceFeatures2(dev, ref feat2);
                    featureQueryRan = true;
                    shaderFloat16 = feat16.shaderFloat16 != 0;

                    var featStorage = new VkPhysicalDevice16BitStorageFeatures
                    {
                        sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_16BIT_STORAGE_FEATURES,
                        pNext = IntPtr.Zero,
                    };
                    feat2.pNext = (IntPtr)(&featStorage);
                    _vkGetPhysicalDeviceFeatures2(dev, ref feat2);
                    storageBuffer16BitAccess = featStorage.storageBuffer16BitAccess != 0;
                    uniformAndStorageBuffer16BitAccess =
                        featStorage.uniformAndStorageBuffer16BitAccess != 0;
                }
            }
            catch
            {
                // Degrade gracefully: featureQueryRan stays false, so an entry point that faulted is
                // treated as "could not be asked" and the vendor heuristic still gets a say.
            }
        }

        uint vendor =
            _vendorIds != null && targetIndex < _vendorIds.Length ? _vendorIds[targetIndex] : 0;
        return ResolveFp16Support(
            featureQueryRan,
            shaderFloat16,
            storageBuffer16BitAccess,
            uniformAndStorageBuffer16BitAccess,
            vendor
        );
    }

    /// <summary>
    /// The FP16-support decision once the Vulkan feature query has (or has not) answered. Split out
    /// from <see cref="SupportsFp16"/> so it can be exercised without a GPU, because the native query
    /// it depends on cannot be driven from a unit test.
    /// <para>
    /// When <paramref name="featureQueryRan"/> is true the three feature flags are authoritative —
    /// including when all are false, so a Vulkan device that explicitly reports no FP16 is never
    /// overridden by its vendor id. The vendor heuristic answers only when the query never ran.
    /// </para>
    /// </summary>
    internal static bool ResolveFp16Support(
        bool featureQueryRan,
        bool shaderFloat16,
        bool storageBuffer16BitAccess,
        bool uniformAndStorageBuffer16BitAccess,
        uint vendorId
    )
    {
        if (featureQueryRan)
        {
            // All three, not either/or. The WebGPU shader-f16 gate on Vulkan requires fp16 arithmetic
            // (VK_KHR_shader_float16_int8's shaderFloat16) *and* 16-bit storage in both the buffer and
            // the uniform-block storage classes (VK_KHR_16bit_storage's storageBuffer16BitAccess and
            // uniformAndStorageBuffer16BitAccess); both Chrome and Firefox require exactly that set,
            // which is why Adreno devices lacking uniformAndStorageBuffer16BitAccess get no
            // shader-f16 (gpuweb#2512, gpuweb#5006). An OR answered yes on a device the EP cannot run
            // the fp16 model on, which selects the fp16 package and hashes the engine identity as
            // fp16 for a backend that then falls back to fp32.
            return shaderFloat16 && storageBuffer16BitAccess && uniformAndStorageBuffer16BitAccess;
        }

        // Known modern GPU vendors: NVIDIA (0x10DE), AMD (0x1002), Intel (0x8086), Apple (0x106B)
        return vendorId is 0x10DE or 0x1002 or 0x8086 or 0x106B;
    }

    /// <summary>
    /// One memory heap, as read from <c>vkGetPhysicalDeviceMemoryProperties2</c>, together with the
    /// budget/usage the driver filled into the chained
    /// <c>VkPhysicalDeviceMemoryBudgetPropertiesEXT</c> (both zero when it did not). Extracted so the
    /// heap-selection decision can be exercised without a GPU.
    /// </summary>
    internal readonly record struct VulkanHeapInfo(
        ulong SizeBytes,
        uint Flags,
        ulong BudgetBytes,
        ulong UsageBytes
    );

    private static unsafe VulkanGpuMemory? QueryDeviceInternal(
        int physicalIndex,
        int acceleratorIndex
    )
    {
        if (
            _physicalDevices == null
            || physicalIndex < 0
            || physicalIndex >= _physicalDevices.Length
        )
        {
            return null;
        }

        IntPtr dev = _physicalDevices[physicalIndex];
        if (dev == IntPtr.Zero || _vkGetPhysicalDeviceMemoryProperties2 == null)
        {
            return null;
        }

        try
        {
            var budgetProps = new VkPhysicalDeviceMemoryBudgetPropertiesEXT
            {
                sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_BUDGET_PROPERTIES_EXT,
                pNext = IntPtr.Zero,
            };

            var memProps2 = new VkPhysicalDeviceMemoryProperties2
            {
                sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_PROPERTIES_2,
                pNext = (IntPtr)(&budgetProps),
            };

            _vkGetPhysicalDeviceMemoryProperties2(dev, ref memProps2);

            int heapCount = (int)Math.Min(memProps2.memoryProperties.memoryHeapCount, 16u);
            var heaps = (VkMemoryHeap*)memProps2.memoryProperties.memoryHeaps;

            Span<VulkanHeapInfo> heapInfos = stackalloc VulkanHeapInfo[heapCount];
            for (int i = 0; i < heapCount; i++)
            {
                heapInfos[i] = new VulkanHeapInfo(
                    heaps[i].size,
                    heaps[i].flags,
                    budgetProps.heapBudget[i],
                    budgetProps.heapUsage[i]
                );
            }

            string name =
                (_deviceNames != null && physicalIndex < _deviceNames.Length)
                    ? _deviceNames[physicalIndex]
                    : $"Vulkan Device {physicalIndex}";
            uint vendorId =
                (_vendorIds != null && physicalIndex < _vendorIds.Length)
                    ? _vendorIds[physicalIndex]
                    : 0;

            return BuildMemoryMetrics(acceleratorIndex, name, vendorId, heapInfos);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the raw heap table to <see cref="VulkanGpuMemory"/>, or returns null when the driver
    /// reported no memory-budget information for any heap.
    /// <para>
    /// <c>VK_EXT_memory_budget</c> needs no enabling for the properties query, but a driver that does
    /// not implement it leaves every chained <c>heapBudget</c> and <c>heapUsage</c> entry at zero.
    /// Those zeros cannot be told apart from a genuinely idle card if they are reported as
    /// <c>UsedVramBytes = 0</c>: <c>OnnxTiler.CalculateVramBudget</c> would then take its
    /// quasi-exclusive branch and budget roughly <c>total − 256 MiB</c> on a desktop that is
    /// actually holding several GB. Returning null instead is what makes
    /// <c>OnnxTiler.GetAvailableVramBudget</c> treat the Vulkan source as having no usable
    /// information and fall through to DRM sysfs / nvidia-smi, which measure actual usage.
    /// </para>
    /// </summary>
    internal static VulkanGpuMemory? BuildMemoryMetrics(
        int acceleratorIndex,
        string deviceName,
        uint vendorId,
        ReadOnlySpan<VulkanHeapInfo> heaps
    )
    {
        long bestVramTotal = 0;
        long bestVramBudget = 0;
        long bestVramUsed = 0;
        long bestGttTotal = 0;
        long bestGttUsed = 0;
        bool anyBudgetInformation = false;

        for (int i = 0; i < heaps.Length; i++)
        {
            VulkanHeapInfo heap = heaps[i];
            if (heap.BudgetBytes != 0 || heap.UsageBytes != 0)
            {
                anyBudgetInformation = true;
            }

            bool isDeviceLocal = (heap.Flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) != 0;

            // On discrete GPUs, the primary VRAM pool is the largest device-local heap.
            // On integrated GPUs / unified memory, all heaps may share memory.
            if (isDeviceLocal && (long)heap.SizeBytes > bestVramTotal)
            {
                bestVramTotal = (long)heap.SizeBytes;
                bestVramBudget = (long)heap.BudgetBytes;
                bestVramUsed = (long)heap.UsageBytes;
            }
            else if (!isDeviceLocal && (long)heap.SizeBytes > bestGttTotal)
            {
                bestGttTotal = (long)heap.SizeBytes;
                bestGttUsed = (long)heap.UsageBytes;
            }
        }

        // No heap carries a budget or a usage figure: the driver never populated the extension
        // struct, so neither the used VRAM nor the free budget is known. Do not guess zero.
        if (!anyBudgetInformation)
        {
            return null;
        }

        // Fallback: If no heap was flagged DEVICE_LOCAL, pick the largest heap overall
        if (bestVramTotal == 0)
        {
            for (int i = 0; i < heaps.Length; i++)
            {
                VulkanHeapInfo heap = heaps[i];
                if ((long)heap.SizeBytes > bestVramTotal)
                {
                    bestVramTotal = (long)heap.SizeBytes;
                    bestVramBudget = (long)heap.BudgetBytes;
                    bestVramUsed = (long)heap.UsageBytes;
                }
            }
        }

        long effectiveUsed =
            bestVramUsed > 0
                ? bestVramUsed
                : (
                    bestVramBudget > 0 && bestVramBudget < bestVramTotal
                        ? bestVramTotal - bestVramBudget
                        : 0
                );

        return new VulkanGpuMemory(
            acceleratorIndex,
            deviceName,
            vendorId,
            bestVramTotal,
            bestVramBudget,
            effectiveUsed,
            bestGttTotal,
            bestGttUsed
        );
    }

    [MemberNotNullWhen(true, nameof(_physicalDevices))]
    private static void EnsureInitialized()
    {
        if (_initAttempted)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initAttempted)
            {
                return;
            }

            _initAttempted = true;
            try
            {
                _vulkanLib = LoadVulkanLibrary();
                if (_vulkanLib == IntPtr.Zero)
                {
                    return;
                }

                if (
                    !NativeLibrary.TryGetExport(
                        _vulkanLib,
                        "vkGetInstanceProcAddr",
                        out var procAddrPtr
                    )
                )
                {
                    return;
                }
                _vkGetInstanceProcAddr =
                    Marshal.GetDelegateForFunctionPointer<VkGetInstanceProcAddrDelegate>(
                        procAddrPtr
                    );

                if (
                    !NativeLibrary.TryGetExport(
                        _vulkanLib,
                        "vkCreateInstance",
                        out var createInstPtr
                    )
                )
                {
                    return;
                }
                var vkCreateInstance =
                    Marshal.GetDelegateForFunctionPointer<VkCreateInstanceDelegate>(createInstPtr);

                var appInfo = new VkApplicationInfo
                {
                    sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                    pApplicationName = IntPtr.Zero,
                    applicationVersion = 0,
                    pEngineName = IntPtr.Zero,
                    engineVersion = 0,
                    apiVersion = (1u << 22) | (1u << 12), // Vulkan 1.1
                };

                IntPtr appInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
                Marshal.StructureToPtr(appInfo, appInfoPtr, false);

                int result;
                try
                {
                    var createInfo = new VkInstanceCreateInfo
                    {
                        sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                        pApplicationInfo = appInfoPtr,
                    };

                    result = vkCreateInstance(in createInfo, IntPtr.Zero, out _instance);
                }
                finally
                {
                    Marshal.FreeHGlobal(appInfoPtr);
                }

                if (result != 0 || _instance == IntPtr.Zero)
                {
                    return;
                }

                // Register process exit hook to cleanly destroy Vulkan instance
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

                IntPtr pfnProps2 = _vkGetInstanceProcAddr(
                    _instance,
                    "vkGetPhysicalDeviceMemoryProperties2"
                );
                if (pfnProps2 == IntPtr.Zero)
                {
                    pfnProps2 = _vkGetInstanceProcAddr(
                        _instance,
                        "vkGetPhysicalDeviceMemoryProperties2KHR"
                    );
                }

                if (pfnProps2 != IntPtr.Zero)
                {
                    _vkGetPhysicalDeviceMemoryProperties2 =
                        Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDeviceMemoryProperties2Delegate>(
                            pfnProps2
                        );
                }

                IntPtr pfnDevProps = _vkGetInstanceProcAddr(
                    _instance,
                    "vkGetPhysicalDeviceProperties"
                );
                if (pfnDevProps != IntPtr.Zero)
                {
                    _vkGetPhysicalDeviceProperties =
                        Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDevicePropertiesDelegate>(
                            pfnDevProps
                        );
                }

                IntPtr pfnFeat2 = _vkGetInstanceProcAddr(_instance, "vkGetPhysicalDeviceFeatures2");
                if (pfnFeat2 == IntPtr.Zero)
                {
                    pfnFeat2 = _vkGetInstanceProcAddr(_instance, "vkGetPhysicalDeviceFeatures2KHR");
                }

                if (pfnFeat2 != IntPtr.Zero)
                {
                    _vkGetPhysicalDeviceFeatures2 =
                        Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDeviceFeatures2Delegate>(
                            pfnFeat2
                        );
                }

                IntPtr pfnEnumDevices = _vkGetInstanceProcAddr(
                    _instance,
                    "vkEnumeratePhysicalDevices"
                );
                if (pfnEnumDevices == IntPtr.Zero)
                {
                    return;
                }
                var vkEnumeratePhysicalDevices =
                    Marshal.GetDelegateForFunctionPointer<VkEnumeratePhysicalDevicesDelegate>(
                        pfnEnumDevices
                    );

                uint deviceCount = 0;
                if (
                    vkEnumeratePhysicalDevices(_instance, ref deviceCount, null) != 0
                    || deviceCount == 0
                )
                {
                    return;
                }

                var devices = new IntPtr[deviceCount];
                if (vkEnumeratePhysicalDevices(_instance, ref deviceCount, devices) != 0)
                {
                    return;
                }

                // The second call may write a smaller count than the first (a device that disappeared
                // between the two queries). Every array is sized from that final count and the pointer
                // array is trimmed to match, because the loops below bound on _physicalDevices.Length
                // and a mismatch indexed the shorter name/vendor/type arrays out of range.
                if (devices.Length != (int)deviceCount)
                {
                    Array.Resize(ref devices, (int)deviceCount);
                }

                _physicalDevices = devices;
                _deviceNames = new string[deviceCount];
                _vendorIds = new uint[deviceCount];
                _deviceTypes = new uint[deviceCount];

                for (int i = 0; i < deviceCount; i++)
                {
                    if (_vkGetPhysicalDeviceProperties != null)
                    {
                        _vkGetPhysicalDeviceProperties(devices[i], out var devProps);
                        _vendorIds[i] = devProps.vendorID;
                        _deviceTypes[i] = devProps.deviceType;

                        unsafe
                        {
                            _deviceNames[i] =
                                Marshal.PtrToStringUTF8((IntPtr)devProps.deviceName)
                                ?? $"Device {i}";
                        }
                    }
                    else
                    {
                        _deviceNames[i] = $"Device {i}";
                    }
                }
            }
            catch
            {
                // Silently handle any initialization failure and degrade gracefully
                Shutdown();
            }
        }
    }

    private static void Shutdown()
    {
        lock (InitLock)
        {
            if (_instance != IntPtr.Zero && _vulkanLib != IntPtr.Zero)
            {
                try
                {
                    if (
                        NativeLibrary.TryGetExport(
                            _vulkanLib,
                            "vkDestroyInstance",
                            out var destroyPtr
                        )
                    )
                    {
                        var vkDestroyInstance =
                            Marshal.GetDelegateForFunctionPointer<VkDestroyInstanceDelegate>(
                                destroyPtr
                            );
                        vkDestroyInstance(_instance, IntPtr.Zero);
                    }
                }
                catch
                {
                    // Ignore shutdown errors
                }
                _instance = IntPtr.Zero;
            }

            _physicalDevices = null;
            _deviceNames = null;
            _vendorIds = null;
            _deviceTypes = null;
        }
    }

    private static IntPtr LoadVulkanLibrary()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (NativeLibrary.TryLoad("vulkan-1.dll", out var handle))
                    return handle;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (NativeLibrary.TryLoad("libvulkan.so.1", out var handle))
                    return handle;
                if (NativeLibrary.TryLoad("libvulkan.so", out handle))
                    return handle;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (NativeLibrary.TryLoad("libvulkan.1.dylib", out var handle))
                    return handle;
                if (NativeLibrary.TryLoad("libvulkan.dylib", out handle))
                    return handle;
                if (NativeLibrary.TryLoad("libMoltenVK.dylib", out handle))
                    return handle;
            }
        }
        catch
        {
            // Ignore native load failures
        }

        return IntPtr.Zero;
    }
}
