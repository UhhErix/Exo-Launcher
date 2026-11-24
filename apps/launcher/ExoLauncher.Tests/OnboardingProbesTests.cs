using System.Diagnostics;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class OnboardingProbesTests
{
    [Fact]
    public async Task PcHealth_ReadWithProbesAsync_ReturnsPartialResultsFastWhenSlowProbesStall()
    {
        var never = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowNetwork = new TaskCompletionSource<NetworkBest.State?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowGpu = new TaskCompletionSource<PcHealth.GpuBar?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedPower = new PowerPlan.State(PowerPlan.Balanced, "Balanced", Guid.Empty, false, false, false);
        var expectedDimms = new[] { new PcHealth.Dimm(6000, 4800, null, 0x09) };

        var sw = Stopwatch.StartNew();
        var reading = await PcHealth.ReadWithProbesAsync(
            cpu: "AMD Ryzen 7 7800X3D 8-Core Processor",
            installed: "6.0.0.0",
            chipset: null,
            chipsetAuto: false,
            fastGpuForGames: false,
            laptop: false,
            readLatestChipset: () => never.Task,
            readPower: () => Task.FromResult<PowerPlan.State?>(expectedPower),
            readNetwork: () => slowNetwork.Task,
            readGpu: () => slowGpu.Task,
            readHybrid: () => Task.FromResult<PcHealth.HybridGpu?>(null),
            readGpuPcie: () => Task.FromResult<PcHealth.PcieLink?>(null),
            readPrimaryOnIntegrated: () => Task.FromResult(false),
            readDimms: () => Task.FromResult<IReadOnlyList<PcHealth.Dimm>>(expectedDimms),
            readBoardMaker: () => "ASUS",
            readHagsOff: () => false,
            chipsetTimeout: TimeSpan.FromMilliseconds(80),
            probeTimeout: TimeSpan.FromMilliseconds(80),
            ct: CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Expected fast partial return, took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(expectedPower, reading.Power);
        Assert.Single(reading.Dimms);
        Assert.Null(reading.ChipsetLatest);
        Assert.Null(reading.Network);
        Assert.Null(reading.Gpu);
    }

    [Fact]
    public async Task PcHealth_ReadWithProbesAsync_RunsIndependentProbesInParallel()
    {
        var testCt = TestContext.Current.CancellationToken;
        async Task<T> Delayed<T>(T value)
        {
            await Task.Delay(90, testCt);
            return value;
        }

        var sw = Stopwatch.StartNew();
        var reading = await PcHealth.ReadWithProbesAsync(
            cpu: "AMD Ryzen 7 9800X3D 8-Core Processor",
            installed: null,
            chipset: null,
            chipsetAuto: false,
            fastGpuForGames: false,
            laptop: false,
            readLatestChipset: () => Delayed<string?>("7.0.0.0"),
            readPower: () => Delayed<PowerPlan.State?>(new PowerPlan.State(PowerPlan.Balanced, "Balanced", Guid.Empty, false, false, false)),
            readNetwork: () => Delayed<NetworkBest.State?>(null),
            readGpu: () => Delayed<PcHealth.GpuBar?>(new PcHealth.GpuBar("NVIDIA GeForce RTX 4090", false, true)),
            readHybrid: () => Delayed<PcHealth.HybridGpu?>(null),
            readGpuPcie: () => Delayed<PcHealth.PcieLink?>(new PcHealth.PcieLink("NVIDIA GeForce RTX 4090", 16, 16)),
            readPrimaryOnIntegrated: () => Task.FromResult(false),
            readDimms: () => Delayed<IReadOnlyList<PcHealth.Dimm>>([]),
            readBoardMaker: () => "MSI",
            readHagsOff: () => false,
            chipsetTimeout: TimeSpan.FromSeconds(2),
            probeTimeout: TimeSpan.FromSeconds(2),
            ct: CancellationToken.None);
        sw.Stop();

        Assert.NotNull(reading.Power);
        Assert.NotNull(reading.Gpu);
        Assert.NotNull(reading.GpuPcie);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(350), $"Probes should run in parallel, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GpuProfile_ReadProfileWithTimeoutAsync_ReturnsPartialStateWhenDriverProbeStalls()
    {
        var gate = new TaskCompletionSource<GpuProfileService.State>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sw = Stopwatch.StartNew();
        var state = await WebHostBridge.ReadProfileWithTimeoutAsync(
            _ => gate.Task,
            () => "nvidia",
            TimeSpan.FromMilliseconds(60),
            CancellationToken.None);
        sw.Stop();

        Assert.False(state.Ok);
        Assert.Equal("none", state.Status);
        Assert.Equal("nvidia", state.Vendor);
        Assert.Contains("still starting", state.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GpuProfile_SharedInFlightRead_ServesWarmedResultOnRetryAfterFirstCallTimesOut()
    {
        var driverReady = new TaskCompletionSource<GpuProfileService.State>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<GpuProfileService.State>? inFlight = null;
        Task<GpuProfileService.State> SharedRead(CancellationToken ct)
        {
            if (inFlight is not { IsCompleted: false })
            {
                Interlocked.Increment(ref reads);
                inFlight = driverReady.Task;
            }
            return inFlight.WaitAsync(ct);
        }

        var first = await WebHostBridge.ReadProfileWithTimeoutAsync(
            SharedRead,
            () => "nvidia",
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(first.Ok);
        Assert.Equal("none", first.Status);

        var retryTask = WebHostBridge.ReadProfileWithTimeoutAsync(
            SharedRead,
            () => "nvidia",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        var full = new GpuProfileService.State(true, null, "nvidia", "RTX 4080", 40, new GpuProfile.Screen("Main", 240, true), "ready", false, false, []);
        driverReady.SetResult(full);

        var retry = await retryTask;

        Assert.True(retry.Ok);
        Assert.Equal("ready", retry.Status);
        Assert.Equal(1, Volatile.Read(ref reads));
    }
}
