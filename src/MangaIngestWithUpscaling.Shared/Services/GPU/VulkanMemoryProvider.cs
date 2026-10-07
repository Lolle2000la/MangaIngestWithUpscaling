using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace MangaIngestWithUpscaling.Shared.Services.GPU;

/// <summary>
/// Represents GPU memory metrics queried directly from the Vulkan graphics driver.
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
    private const uint VK_STRUCTURE_TYPE_APPLICATION_INFO = 1;
    private const uint VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 10;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2 = 1000059000;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_PROPERTIES_2 = 1000059006;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_FLOAT16_INT8_FEATURES = 1000082000;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_16BIT_STORAGE_FEATURES = 1000083000;
    private const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_BUDGET_PROPERTIES_EXT = 1000237000;
    private const uint VK_MEMORY_HEAP_DEVICE_LOCAL_BIT = 0x00000001;

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
    /// Gets the count of discovered Vulkan physical devices.
    /// </summary>
    public static int DeviceCount
    {
        get
        {
            EnsureInitialized();
            return _physicalDevices?.Length ?? 0;
        }
    }

    /// <summary>
    /// Queries memory metrics for all discovered Vulkan devices.
    /// </summary>
    public static IReadOnlyList<VulkanGpuMemory> QueryAllDevices()
    {
        EnsureInitialized();
        if (!IsAvailable || _physicalDevices == null)
        {
            return Array.Empty<VulkanGpuMemory>();
        }

        var list = new List<VulkanGpuMemory>(_physicalDevices.Length);
        for (int i = 0; i < _physicalDevices.Length; i++)
        {
            var mem = QueryDeviceInternal(i);
            if (mem != null)
            {
                list.Add(mem);
            }
        }

        return list;
    }

    /// <summary>
    /// Queries memory metrics (total VRAM, driver budget, used VRAM) for the specified Vulkan device.
    /// Returns null if Vulkan is unavailable or the device index is out of range.
    /// </summary>
    public static VulkanGpuMemory? QueryDevice(int deviceIndex = 0)
    {
        EnsureInitialized();
        if (!IsAvailable || _physicalDevices == null)
        {
            return null;
        }

        int targetIndex =
            (deviceIndex >= 0 && deviceIndex < _physicalDevices.Length) ? deviceIndex : 0;

        return QueryDeviceInternal(targetIndex);
    }

    /// <summary>
    /// Checks whether the specified Vulkan physical device is a software CPU device (e.g. llvmpipe, lavapipe).
    /// </summary>
    public static bool IsCpuDevice(int deviceIndex = 0)
    {
        EnsureInitialized();
        if (!IsAvailable || _physicalDevices == null || _deviceTypes == null)
        {
            return false;
        }

        int targetIndex =
            (deviceIndex >= 0 && deviceIndex < _physicalDevices.Length) ? deviceIndex : 0;

        return _deviceTypes[targetIndex] == 4; // VK_PHYSICAL_DEVICE_TYPE_CPU
    }

    /// <summary>
    /// Checks whether the specified Vulkan physical device supports native 16-bit floating point (FP16) arithmetic.
    /// Returns false if Vulkan is unavailable, the device is a software CPU rasterizer, or FP16 is not supported.
    /// </summary>
    public static bool SupportsFp16(int deviceIndex = 0)
    {
        EnsureInitialized();
        if (!IsAvailable || _physicalDevices == null)
        {
            return false;
        }

        int targetIndex =
            (deviceIndex >= 0 && deviceIndex < _physicalDevices.Length) ? deviceIndex : 0;

        // Disqualify CPU software rasterizers
        if (IsCpuDevice(targetIndex))
        {
            return false;
        }

        IntPtr dev = _physicalDevices[targetIndex];
        if (dev == IntPtr.Zero)
        {
            return false;
        }

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
                    if (feat16.shaderFloat16 != 0)
                    {
                        return true;
                    }

                    var featStorage = new VkPhysicalDevice16BitStorageFeatures
                    {
                        sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_16BIT_STORAGE_FEATURES,
                        pNext = IntPtr.Zero,
                    };
                    feat2.pNext = (IntPtr)(&featStorage);
                    _vkGetPhysicalDeviceFeatures2(dev, ref feat2);
                    if (featStorage.storageBuffer16BitAccess != 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Degrade gracefully
            }
        }

        // Fallback for hardware GPUs if feature extension query failed:
        // Known modern GPU vendors: NVIDIA (0x10DE), AMD (0x1002), Intel (0x8086), Apple (0x106B)
        uint vendor =
            _vendorIds != null && targetIndex < _vendorIds.Length ? _vendorIds[targetIndex] : 0;
        return vendor is 0x10DE or 0x1002 or 0x8086 or 0x106B;
    }

    private static unsafe VulkanGpuMemory? QueryDeviceInternal(int index)
    {
        if (_physicalDevices == null || index < 0 || index >= _physicalDevices.Length)
        {
            return null;
        }

        IntPtr dev = _physicalDevices[index];
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

            uint heapCount = memProps2.memoryProperties.memoryHeapCount;
            long bestVramTotal = 0;
            long bestVramBudget = 0;
            long bestVramUsed = 0;
            long bestGttTotal = 0;
            long bestGttUsed = 0;

            var heaps = (VkMemoryHeap*)memProps2.memoryProperties.memoryHeaps;
            for (int i = 0; i < heapCount && i < 16; i++)
            {
                var heap = heaps[i];
                bool isDeviceLocal = (heap.flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) != 0;

                // On discrete GPUs, the primary VRAM pool is the largest device-local heap.
                // On integrated GPUs / unified memory, all heaps may share memory.
                if (isDeviceLocal && (long)heap.size > bestVramTotal)
                {
                    bestVramTotal = (long)heap.size;
                    bestVramBudget = (long)budgetProps.heapBudget[i];
                    bestVramUsed = (long)budgetProps.heapUsage[i];
                }
                else if (!isDeviceLocal && (long)heap.size > bestGttTotal)
                {
                    bestGttTotal = (long)heap.size;
                    bestGttUsed = (long)budgetProps.heapUsage[i];
                }
            }

            // Fallback: If no heap was flagged DEVICE_LOCAL, pick the largest heap overall
            if (bestVramTotal == 0)
            {
                for (int i = 0; i < heapCount && i < 16; i++)
                {
                    var heap = heaps[i];
                    if ((long)heap.size > bestVramTotal)
                    {
                        bestVramTotal = (long)heap.size;
                        bestVramBudget = (long)budgetProps.heapBudget[i];
                        bestVramUsed = (long)budgetProps.heapUsage[i];
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

            string name =
                (_deviceNames != null && index < _deviceNames.Length)
                    ? _deviceNames[index]
                    : $"Vulkan Device {index}";
            uint vendorId =
                (_vendorIds != null && index < _vendorIds.Length) ? _vendorIds[index] : 0;

            return new VulkanGpuMemory(
                index,
                name,
                vendorId,
                bestVramTotal,
                bestVramBudget,
                effectiveUsed,
                bestGttTotal,
                bestGttUsed
            );
        }
        catch
        {
            return null;
        }
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
