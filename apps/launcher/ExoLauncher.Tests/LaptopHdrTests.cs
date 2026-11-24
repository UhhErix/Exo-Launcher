using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LaptopHdrTests
{
    [Theory]
    [InlineData(0xFFFFFFFFu, "", false)]
    [InlineData(0u, "", false)]
    [InlineData(1u, "", false)]
    [InlineData(2u, "", false)]
    [InlineData(3u, "", false)]
    [InlineData(4u, "dvi", false)]
    [InlineData(5u, "hdmi", false)]
    [InlineData(6u, "", true)]
    [InlineData(8u, "", false)]
    [InlineData(9u, "", false)]
    [InlineData(10u, "dp", false)]
    [InlineData(11u, "dp", true)]
    [InlineData(12u, "", false)]
    [InlineData(13u, "", false)]
    [InlineData(14u, "", false)]
    [InlineData(15u, "", false)]
    [InlineData(16u, "", false)]
    [InlineData(17u, "", false)]
    [InlineData(18u, "dp", false)]
    [InlineData(0x80000000u, "", true)]
    public void Classifies_connector_and_internal_panel_for_every_output_technology(uint technology, string expectedConnector, bool expectedInternal)
    {
        Assert.Equal(expectedConnector, WindowsHdr.Connector(technology));
        Assert.Equal(expectedInternal, WindowsHdr.IsInternalTechnology(technology));
    }

    [Theory]
    [InlineData(true, 1000, 1000, "ok", false)]
    [InlineData(true, 1000, 1100, "ok", false)]
    [InlineData(true, 1000, 900, "ok", false)]
    [InlineData(true, 1000, 1101, "mismatch", true)]
    [InlineData(true, 1260, 160, "mismatch", true)]
    [InlineData(true, 1260, 13440, "mismatch", true)]
    [InlineData(false, 1000, 1050, "ok", false)]
    [InlineData(false, 1000, 1400, "reported", false)]
    [InlineData(true, null, 1000, "unknown", false)]
    [InlineData(true, 1000, null, "unknown", false)]
    [InlineData(false, 0, 1000, "unknown", false)]
    public void Peak_health_check_enforces_ten_percent_threshold_and_internal_only_warning(
        bool isInternal, int? edid, int? dxgi, string expectedStatus, bool expectWarning)
    {
        var health = HdrDisplayHealth.Evaluate(isInternal, edid, dxgi);
        Assert.Equal(expectedStatus, health.Status);
        if (expectWarning)
        {
            Assert.NotNull(health.Warning);
            Assert.Contains(edid!.Value.ToString(), health.Warning);
            Assert.Contains(dxgi!.Value.ToString(), health.Warning);
        }
        else
        {
            Assert.Null(health.Warning);
        }
    }

    [Fact]
    public void Laptop_checks_acm_and_autohdr_behave_honestly()
    {
        Assert.Equal("sdr", WindowsHdr.ActiveColorModeName(0));
        Assert.Equal("acm", WindowsHdr.ActiveColorModeName(1));
        Assert.Equal("hdr", WindowsHdr.ActiveColorModeName(2));
        Assert.Null(WindowsHdr.ActiveColorModeName(99));

        Assert.True(LaptopDisplayChecks.ParseAutoHdrEnable("GpuPreference=2;AutoHDREnable=2097;"));
        Assert.False(LaptopDisplayChecks.ParseAutoHdrEnable("AutoHDREnable=2096;"));
        Assert.False(LaptopDisplayChecks.ParseAutoHdrEnable("AutoHDREnable=0;"));
        Assert.True(LaptopDisplayChecks.ParseAutoHdrEnable("SwapEffectUpgradeEnable=1;AutoHDREnable=1;"));
        Assert.Null(LaptopDisplayChecks.ParseAutoHdrEnable("GpuPreference=2;"));

        Assert.Equal("off", LaptopDisplayChecks.ParseCabcOption(0));
        Assert.Equal("active", LaptopDisplayChecks.ParseCabcOption(1));
        Assert.Equal("battery_only", LaptopDisplayChecks.ParseCabcOption(2));
        Assert.Equal("unknown", LaptopDisplayChecks.ParseCabcOption(null));

        Assert.Equal("active", LaptopDisplayChecks.ParseIntelFeatureTestControl(0x9240));
        Assert.Equal("off", LaptopDisplayChecks.ParseIntelFeatureTestControl(0x9250));
        Assert.Equal("unknown", LaptopDisplayChecks.ParseIntelFeatureTestControl(null));

        var pluggedHdr = LaptopDisplayChecks.Evaluate(new LaptopDisplayChecks.RawSignals(
            IsLaptop: true, PluggedIn: true, HdrOn: true, ColorMode: "hdr", AutoHdrGlobal: true,
            BatterySaver: "active", Cabc: "active", IntelDpst: "unknown", AmdVariBright: "active"));
        Assert.True(pluggedHdr.Items.Single(i => i.Id == "batterySaver").Warn);
        Assert.True(pluggedHdr.Items.Single(i => i.Id == "cabc").Warn);
        Assert.True(pluggedHdr.Items.Single(i => i.Id == "amdVariBright").Warn);
        var dpst = pluggedHdr.Items.Single(i => i.Id == "intelDpst");
        Assert.Equal("unknown", dpst.Status);
        Assert.False(dpst.Warn);
        Assert.Contains("Can't detect", dpst.Detail);

        var onBattery = LaptopDisplayChecks.Evaluate(new LaptopDisplayChecks.RawSignals(
            IsLaptop: true, PluggedIn: false, HdrOn: true, ColorMode: "hdr", AutoHdrGlobal: null,
            BatterySaver: "active", Cabc: "battery_only", IntelDpst: "not_applicable", AmdVariBright: "not_applicable"));
        Assert.All(onBattery.Items, item => Assert.False(item.Warn));
    }
}
