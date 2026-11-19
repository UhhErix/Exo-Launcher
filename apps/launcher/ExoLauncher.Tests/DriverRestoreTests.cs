using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways putting things back after a graphics driver install could go wrong silently:
/// - a screen's saved settings land on another screen because DISPLAYn renumbered, or are lost because the install gave
///   the monitor a new device path; two monitors of one model trade settings;
/// - RTX Video isn't set back to the user's choice, Exo's built-in Auto overrides a choice the user made in Exo, or HDR the
///   user never chose in Exo is changed;
/// - a Refresh, a plan check or a paused queue of Exo Shaders holds a driver install as if it were compiling, or a running
///   compile doesn't.
/// </summary>
public sealed class DriverRestoreTests
{
    private const string Onn = @"\\?\DISPLAY#ONN0101#5&19b437dc&0&UID28930#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string OnnNew = @"\\?\DISPLAY#ONN0101#5&2a11f00d&0&UID28931#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Dell = @"\\?\DISPLAY#DEL4321#5&19b437dc&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string DellNew = @"\\?\DISPLAY#DEL4321#5&77aa0001&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string DellTwin = @"\\?\DISPLAY#DEL4321#5&77aa0002&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void AScreenIsFoundByItsMonitorNotItsNumber()
    {
        // The same path wherever Windows put it in the list (DISPLAY6 and DISPLAY7 after a clean install).
        Assert.Equal(Onn, DriverRestore.SameMonitor(Onn, [Dell, Onn], [Onn, Dell]));
        Assert.Equal(Dell, DriverRestore.SameMonitor(Dell.ToLowerInvariant(), [Onn, Dell], [Onn, Dell]));
        // A new instance for the same monitor model, the only one missing: it moves there.
        Assert.Equal(OnnNew, DriverRestore.SameMonitor(Onn, [OnnNew, Dell], [Onn, Dell]));
        // Gone: nothing.
        Assert.Null(DriverRestore.SameMonitor(Onn, [Dell], [Onn, Dell]));
        // Two screens of one model with new paths: unclear, so neither takes another's settings.
        Assert.Null(DriverRestore.SameMonitor(Dell, [DellNew, DellTwin], [Dell]));
        Assert.Null(DriverRestore.SameMonitor(Dell, [DellNew, Onn], [Dell, DellTwin]));
        // A screen another saved path still matches is never taken.
        Assert.Null(DriverRestore.SameMonitor(Dell, [DellTwin], [Dell, DellTwin]));
    }

    [Fact]
    public void RtxVideoGoesBackToTheChoice()
    {
        // Nothing chosen in Exo: Auto, Exo's built-in default; HDR stays as the driver has it.
        Assert.Equal([(NvidiaVideo.SuperResolution, 5)], DriverRestore.RtxWrites(0, 0, null, null));
        Assert.Empty(DriverRestore.RtxWrites(5, 5, null, null));
        // The user's own choice wins over Auto, off included.
        Assert.Equal([(NvidiaVideo.SuperResolution, 3)], DriverRestore.RtxWrites(0, null, 3, null));
        Assert.Empty(DriverRestore.RtxWrites(0, 0, 0, null));
        // HDR only when the user chose it; any value but 0 reads as on.
        Assert.Equal([(NvidiaVideo.VideoHdr, 5)], DriverRestore.RtxWrites(5, 0, null, true));
        Assert.Empty(DriverRestore.RtxWrites(5, 1, null, true));
        Assert.Equal([(NvidiaVideo.SuperResolution, 4), (NvidiaVideo.VideoHdr, 0)], DriverRestore.RtxWrites(0, 5, 4, false));
        // A driver with no HDR component: nothing to set there.
        Assert.Empty(DriverRestore.RtxWrites(5, null, null, true));
    }

    [Theory]
    [InlineData("""{"app":{"status":"compiling"}}""", true)]
    [InlineData("""{"app":{"status":"scanning"}}""", false)]
    [InlineData("""{"app":{"status":"checking"}}""", false)]
    [InlineData("""{"app":{"status":"paused"}}""", false)]
    [InlineData("""{"app":{"status":"idle"}}""", false)]
    [InlineData("""{"app":{}}""", false)]
    [InlineData("""{"app":{"status":1}}""", false)]
    [InlineData("null", false)]
    public void OnlyACompileHoldsADriverInstall(string snapshot, bool compiling) =>
        Assert.Equal(compiling, ShadersAddon.Compiling(JsonNode.Parse(snapshot)));
}
