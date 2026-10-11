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
