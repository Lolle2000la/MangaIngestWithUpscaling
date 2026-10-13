using System.Runtime.InteropServices;
using MangaIngestWithUpscaling.Shared.Services.GPU;
using Xunit;
using VulkanHeapInfo = MangaIngestWithUpscaling.Shared.Services.GPU.VulkanMemoryProvider.VulkanHeapInfo;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.GPU;

public class VulkanMemoryProviderTests
{
    private const string NoVulkan =
        "no Vulkan driver in this environment, so there is nothing to assert about";

    private const string NoBudgetInformation =
        "Vulkan is up but the driver reports no memory-budget information for any accelerator, so "
        + "QueryDevice falls through to DRM sysfs / nvidia-smi and has no metrics to assert about";

    private const uint DeviceLocalBit = 0x00000001; // VK_MEMORY_HEAP_DEVICE_LOCAL_BIT

    [Fact]
    [Trait("Category", "Unit")]
    public void MemoryProbeStructs_FollowThePointerSizeInsteadOfHardcodedOffsets()
    {
        // The two probe structs that carry an IntPtr pNext are Sequential, not explicit offsets:
        // pNext sits at IntPtr.Size and everything after it moves with it, so a 32-bit runtime
        // reads the native layout instead of a skewed one (pNext @ 4, not @ 8). On 64-bit the
        // computed offsets are exactly the ones the explicit layout used to hardcode, which is
        // what the assertions below state without assuming the bitness they run on.
        Assert.Equal(
            (nint)IntPtr.Size,
            Marshal.OffsetOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties2>(
                nameof(VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties2.pNext)
            )
        );
        Assert.Equal(
            (nint)(2 * IntPtr.Size),
            Marshal.OffsetOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties2>(
                nameof(VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties2.memoryProperties)
            )
        );
        Assert.Equal(
            (nint)(2 * IntPtr.Size),
            Marshal.OffsetOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryBudgetPropertiesEXT>(
                nameof(VulkanMemoryProvider.VkPhysicalDeviceMemoryBudgetPropertiesEXT.heapBudget)
            )
        );
        Assert.Equal(
            2 * IntPtr.Size + 520,
            Marshal.SizeOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties2>()
        );
        Assert.Equal(
            2 * IntPtr.Size + 256,
            Marshal.SizeOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryBudgetPropertiesEXT>()
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MemoryPropertiesStruct_PinsThe64BitHeapStrideTheReaderIsGuardedTo()
    {
        // VkPhysicalDeviceMemoryProperties is read through an explicit layout whose VkMemoryHeap
        // stride is 16 bytes — the native one on a 64-bit ABI only (the 8-byte VkDeviceSize member
        // forces it). A 32-bit ABI can use 12, so QueryDeviceInternal returns null (no Vulkan metrics)
        // below 64-bit rather than mis-striding the heap table; OnnxTiler then falls through to DRM
        // sysfs / nvidia-smi. This pins the layout that guard exists for.
        Assert.Equal(
            (nint)264,
            Marshal.OffsetOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties>(
                nameof(VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties.memoryHeapCount)
            ) + sizeof(uint)
        );
        Assert.Equal(520, Marshal.SizeOf<VulkanMemoryProvider.VkPhysicalDeviceMemoryProperties>());
    }

    [Fact]
    public void IsAvailable_DoesNotThrow()
    {
        // Calling IsAvailable should probe safely and never throw an unhandled native exception
        _ = VulkanMemoryProvider.IsAvailable;
    }

    [Fact]
    public void QueryAllDevices_WhenAvailable_ReturnsValidMemoryMetrics()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        var devices = VulkanMemoryProvider.QueryAllDevices();
        Assert.SkipWhen(devices.Count == 0, NoBudgetInformation);
        Assert.NotEmpty(devices);

        foreach (var dev in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(dev.DeviceName));
            Assert.True(
                dev.TotalVramBytes > 0,
                $"Device {dev.DeviceName} reports positive total VRAM"
            );
            // If the driver supports VK_EXT_memory_budget, budget should be > 0 and <= total. The
            // extension is not guaranteed, and a driver without it leaves the budget zeroed, so
            // this is only an upper bound when the driver reports a budget at all.
            if (dev.BudgetBytes > 0)
            {
                Assert.True(
                    dev.BudgetBytes <= dev.TotalVramBytes,
                    $"Budget ({dev.BudgetBytes} bytes) cannot exceed total VRAM ({dev.TotalVramBytes} bytes) for device {dev.DeviceName}"
                );
            }
        }
    }

    [Fact]
    public void QueryDevice_DefaultIndex_MatchesFirstDevice()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        var first = VulkanMemoryProvider.QueryDevice(0);
        Assert.SkipWhen(first is null, NoBudgetInformation);
        Assert.True(first!.TotalVramBytes > 0);
        // The budget requires VK_EXT_memory_budget; its absence is a legitimate "unknown", not a
        // failure, so only the relationship is asserted when the driver reports one.
        if (first.BudgetBytes > 0)
        {
            Assert.True(first.BudgetBytes <= first.TotalVramBytes);
        }
        // DeviceIndex is the accelerator the caller asked about, not the physical device it resolved
        // to. QueryDevice(0) is therefore accelerator 0 on every host, including one where a software
        // rasterizer is enumerated before the GPU and the resolved physical index is 1.
        Assert.Equal(0, first.DeviceIndex);
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceName));
    }

    [Fact]
    public void SupportsFp16_WhenAvailable_ReturnsExpectedSupport()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        bool supportsFp16 = VulkanMemoryProvider.SupportsFp16(0);

        // IsCpuDevice takes an accelerator index and ResolveGpuIndex skips software rasterizers by
        // construction, so IsCpuDevice is false for every accelerator and the old
        // "if (IsCpuDevice(0)) ..." branch could never run. What is left to pin is the host that
        // enumerated rasterizers and no accelerator at all: SupportsFp16 there must be false rather
        // than fall back to the vendor heuristic.
        if (VulkanMemoryProvider.DeviceCount == 0)
        {
            Assert.False(supportsFp16);
        }

        // Nothing is asserted for a real GPU: fp16 is optional in Vulkan and every older
        // integrated part lacks it, so "supported" would be a claim about the hardware, not about
        // this code. What is pinned is that the answer is a bool and does not throw.
    }

    /// <summary>
    /// The fp16 decision after the Vulkan feature query. The native query cannot be driven from a
    /// unit test, so the decision is pinned through the pure helper: it cannot cover the P/Invoke
    /// call itself, only how its answer (or its absence) is interpreted.
    /// </summary>
    [Theory]
    [InlineData((uint)0x10DE)] // NVIDIA
    [InlineData((uint)0x1002)] // AMD
    [InlineData((uint)0x8086)] // Intel
    [InlineData((uint)0x106B)] // Apple
    public void ResolveFp16Support_QueryRanAndSaidNo_IsNotOverriddenByVendorId(uint vendorId)
    {
        // vkGetPhysicalDeviceFeatures2 succeeded and reported none of the three fp16 features. That is
        // the driver saying "not supported", so the vendor heuristic must not turn it back into a yes
        // — otherwise the fp16-only model set is downloaded and the WebGPU EP cannot run it.
        Assert.False(
            VulkanMemoryProvider.ResolveFp16Support(
                featureQueryRan: true,
                shaderFloat16: false,
                storageBuffer16BitAccess: false,
                uniformAndStorageBuffer16BitAccess: false,
                vendorId: vendorId
            )
        );
    }

    [Theory]
    [InlineData((uint)0x10DE)] // NVIDIA
    [InlineData((uint)0x1002)] // AMD
    [InlineData((uint)0x8086)] // Intel
    [InlineData((uint)0x106B)] // Apple
    public void ResolveFp16Support_QueryDidNotRun_FallsBackToVendorHeuristic(uint vendorId)
    {
        // With no entry point (or a query that faulted) the device cannot be asked, so the four
        // known modern vendors are assumed capable.
        Assert.True(
            VulkanMemoryProvider.ResolveFp16Support(
                featureQueryRan: false,
                shaderFloat16: false,
                storageBuffer16BitAccess: false,
                uniformAndStorageBuffer16BitAccess: false,
                vendorId: vendorId
            )
        );
    }

    [Fact]
    public void ResolveFp16Support_QueryDidNotRun_UnknownVendor_IsFalse()
    {
        // The heuristic only ever answers for the vendors it names; an unknown vendor stays "no".
        Assert.False(
            VulkanMemoryProvider.ResolveFp16Support(
                featureQueryRan: false,
                shaderFloat16: false,
                storageBuffer16BitAccess: false,
                uniformAndStorageBuffer16BitAccess: false,
                vendorId: 0x1234
            )
        );
    }

    [Theory]
    [InlineData(true, true, true, true)] // the full set the WebGPU EP requires
    [InlineData(true, true, false, false)] // no 16-bit access in the uniform storage class
    [InlineData(true, false, true, false)] // no 16-bit access in storage buffers
    [InlineData(false, true, true, false)] // no fp16 arithmetic
    [InlineData(true, false, false, false)] // arithmetic only
    [InlineData(false, false, true, false)] // storage only
    public void ResolveFp16Support_QueryRan_RequiresAllThreeFeatures(
        bool shaderFloat16,
        bool storageBuffer16BitAccess,
        bool uniformAndStorageBuffer16BitAccess,
        bool expected
    )
    {
        // The WebGPU shader-f16 gate on Vulkan is fp16 arithmetic *and* 16-bit storage in both the
        // storage-buffer and the uniform-block storage classes — both Chrome and Firefox require
        // exactly that set (gpuweb#2512, gpuweb#5006), which is why Adreno devices without
        // uniformAndStorageBuffer16BitAccess never get shader-f16. An OR answered yes for a device the
        // EP cannot run the fp16 model on, which selects the fp16 package and hashes the engine
        // identity as fp16 for a backend that then falls back to fp32.
        Assert.Equal(
            expected,
            VulkanMemoryProvider.ResolveFp16Support(
                featureQueryRan: true,
                shaderFloat16: shaderFloat16,
                storageBuffer16BitAccess: storageBuffer16BitAccess,
                uniformAndStorageBuffer16BitAccess: uniformAndStorageBuffer16BitAccess,
                vendorId: 0x1234
            )
        );
    }

    [Fact]
    public void ResolveGpuIndex_SkipsSoftwareRasterizers()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        // vkEnumeratePhysicalDevices lists software rasterizers alongside the GPUs, while an
        // accelerator index counts only the GPUs the inference runtime can select. Walking only real
        // GPUs is what keeps the two apart, so the accelerator index can never land on a software
        // device — which with a lavapipe package installed is what it otherwise would do.
        List<int> resolved = new();
        for (int accelerator = 0; accelerator < VulkanMemoryProvider.DeviceCount; accelerator++)
        {
            int vulkan = VulkanMemoryProvider.ResolveGpuIndex(accelerator);
            Assert.True(vulkan >= 0, $"accelerator {accelerator} did not resolve");
            Assert.False(
                VulkanMemoryProvider.IsCpuDevice(accelerator),
                $"accelerator {accelerator} resolved to a software rasterizer"
            );
            resolved.Add(vulkan);
        }

        Assert.NotEmpty(resolved);
        Assert.Equal(resolved.Distinct().Count(), resolved.Count);
        Assert.Equal(resolved.OrderBy(index => index).ToList(), resolved);
    }

    [Fact]
    public void QueryDevice_IsIndexedByAcceleratorIndex()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        // Every caller hands the provider an accelerator index, so QueryDevice(i) has to be the
        // accelerator-th GPU, not the i-th physical device. QueryAllDevices returns the same devices
        // in the same order, having dropped the accelerators whose driver reported nothing.
        IReadOnlyList<VulkanGpuMemory> devices = VulkanMemoryProvider.QueryAllDevices();
        Assert.SkipWhen(devices.Count == 0, NoBudgetInformation);

        List<VulkanGpuMemory> queried = new();
        for (int accelerator = 0; accelerator < VulkanMemoryProvider.DeviceCount; accelerator++)
        {
            if (VulkanMemoryProvider.QueryDevice(accelerator) is VulkanGpuMemory device)
            {
                // DeviceIndex reports the accelerator, not the resolved physical device index, so
                // it matches the accelerator that was asked for even when a rasterizer sorts first.
                Assert.Equal(accelerator, device.DeviceIndex);
                queried.Add(device);
            }
        }

        Assert.Equal(queried.Select(d => d.DeviceName), devices.Select(d => d.DeviceName));
    }

    /// <summary>
    /// The provider talks to native Vulkan, so the decision that a driver reported no budget
    /// information is pinned through the pure mapping helper rather than through a GPU. What these
    /// tests cover is the mapping from raw heap tables to a result (or null); they cannot cover the
    /// P/Invoke call itself, the loader, or <c>OnnxTiler.GetAvailableVramBudget</c>'s fall-through
    /// (which is exercised by the OnnxTiler tests instead).
    /// </summary>
    [Fact]
    public void BuildMemoryMetrics_NoBudgetInformation_IsNotReportedAsIdle()
    {
        // A driver without VK_EXT_memory_budget leaves the chained budget struct zeroed: every heap
        // reports budget 0 and usage 0. Returning a result with UsedVramBytes = 0 would let
        // OnnxTiler.CalculateVramBudget take its quasi-exclusive branch (used <= 350 MB) and budget
        // total - 256 MiB on a desktop that is actually holding several GB. The provider must
        // instead report "unknown" so GetAvailableVramBudget falls through to DRM sysfs / nvidia-smi.
        VulkanHeapInfo[] heaps =
        {
            new(8UL * 1024 * 1024 * 1024, DeviceLocalBit, 0, 0),
            new(16UL * 1024 * 1024 * 1024, 0, 0, 0),
        };

        VulkanGpuMemory? metrics = VulkanMemoryProvider.BuildMemoryMetrics(0, "gpu", 0x1002, heaps);

        Assert.Null(metrics);
    }

    [Fact]
    public void BuildMemoryMetrics_BudgetReportedButUnused_KeepsAnIdleCard()
    {
        // The contrast with the test above: when the driver does populate the extension, a card with
        // nothing allocated is genuinely idle and must stay a positive result so its driver budget
        // can be trusted directly.
        VulkanHeapInfo[] heaps =
        {
            new(8UL * 1024 * 1024 * 1024, DeviceLocalBit, 8UL * 1024 * 1024 * 1024, 0),
        };

        VulkanGpuMemory? metrics = VulkanMemoryProvider.BuildMemoryMetrics(0, "gpu", 0x10DE, heaps);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics!.UsedVramBytes);
        Assert.Equal(8L * 1024 * 1024 * 1024, metrics.BudgetBytes);
    }

    [Fact]
    public void BuildMemoryMetrics_SelectsLargestDeviceLocalHeap()
    {
        VulkanHeapInfo[] heaps =
        {
            new(1UL << 30, DeviceLocalBit, 900UL << 20, 100UL << 20),
            new(8UL << 30, DeviceLocalBit, 6UL << 30, 2UL << 30),
            new(16UL << 30, 0, 0, 3UL << 30),
        };

        VulkanGpuMemory? metrics = VulkanMemoryProvider.BuildMemoryMetrics(2, "gpu", 0x8086, heaps);

        Assert.NotNull(metrics);
        Assert.Equal(2, metrics!.DeviceIndex);
        Assert.Equal(8L << 30, metrics.TotalVramBytes);
        Assert.Equal(6L << 30, metrics.BudgetBytes);
        Assert.Equal(2L << 30, metrics.UsedVramBytes);
        Assert.Equal(16L << 30, metrics.TotalGttBytes);
        Assert.Equal(3L << 30, metrics.UsedGttBytes);
    }

    [Fact]
    public void BuildMemoryMetrics_BudgetWithoutUsage_DerivesUsedFromBudget()
    {
        // Some drivers report the budget but leave usage at zero while memory is held by other
        // processes; total - budget is then the used figure and must not be lost.
        VulkanHeapInfo[] heaps = { new(8UL << 30, DeviceLocalBit, 6UL << 30, 0) };

        VulkanGpuMemory? metrics = VulkanMemoryProvider.BuildMemoryMetrics(0, "gpu", 0x1002, heaps);

        Assert.NotNull(metrics);
        Assert.Equal(8L << 30, metrics!.TotalVramBytes);
        Assert.Equal(6L << 30, metrics.BudgetBytes);
        Assert.Equal(2L << 30, metrics.UsedVramBytes);
    }

    [Fact]
    public void BuildMemoryMetrics_UsageWithoutBudget_KeepsUsage()
    {
        VulkanHeapInfo[] heaps = { new(8UL << 30, DeviceLocalBit, 0, 3UL << 30) };

        VulkanGpuMemory? metrics = VulkanMemoryProvider.BuildMemoryMetrics(0, "gpu", 0x10DE, heaps);

        Assert.NotNull(metrics);
        Assert.Equal(3L << 30, metrics!.UsedVramBytes);
        Assert.Equal(0, metrics.BudgetBytes);
    }

    [Fact]
    public void BuildMemoryMetrics_NoDeviceLocalHeap_FallsBackToLargestHeap()
    {
        VulkanHeapInfo[] heaps =
        {
            new(4UL << 30, 0, 0, 1UL << 30),
            new(8UL << 30, 0, 3UL << 30, 2UL << 30),
        };

        VulkanGpuMemory? metrics = VulkanMemoryProvider.BuildMemoryMetrics(0, "gpu", 0x1002, heaps);

        Assert.NotNull(metrics);
        Assert.Equal(8L << 30, metrics!.TotalVramBytes);
        Assert.Equal(3L << 30, metrics.BudgetBytes);
        Assert.Equal(2L << 30, metrics.UsedVramBytes);
    }
}
