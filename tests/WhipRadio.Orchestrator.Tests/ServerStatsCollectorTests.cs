using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Tests;

[TestClass]
public class ServerStatsCollectorTests
{
    [TestMethod]
    public void NormalizeGpuMemoryUsedMb_KeepsPlausibleNvidiaSmiMiBValue()
    {
        var normalized = ServerStatsCollector.NormalizeGpuMemoryUsedMb(8192, 12282);

        Assert.Equal(8192, normalized);
    }

    [TestMethod]
    public void NormalizeGpuMemoryUsedMb_ConvertsPlausibleByteValue()
    {
        var normalized = ServerStatsCollector.NormalizeGpuMemoryUsedMb(8L * 1024 * 1024 * 1024, 12282);

        Assert.Equal(8192, normalized);
    }

    [TestMethod]
    public void NormalizeGpuMemoryUsedMb_RejectsImpossibleDriverValue()
    {
        var normalized = ServerStatsCollector.NormalizeGpuMemoryUsedMb(17_592_181_862_786, 12282);

        Assert.Null(normalized);
    }

    [TestMethod]
    public void ParseWindowsDedicatedGpuMemoryUsedMb_UsesLargestPlausibleAdapterValue()
    {
        const string TypePerfOutput = """
            "(PDH-CSV 4.0)","\\MACHINE\GPU Adapter Memory(luid_0x00000000_0x0001832C_phys_0)\Dedicated Usage","\\MACHINE\GPU Adapter Memory(luid_0x00000000_0x0001A390_phys_0)\Dedicated Usage"
            "06/19/2026 02:14:35.762","8589934592.000000","0.000000"
            Exiting, please wait...
            The command completed successfully.
            """;

        var usedMb = ServerStatsCollector.ParseWindowsDedicatedGpuMemoryUsedMb(TypePerfOutput, 12282);

        Assert.Equal(8192, usedMb);
    }
}
