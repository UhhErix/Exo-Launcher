namespace ExoLauncher.Helpers;

/// <summary>
/// The GPU's live readings (Auto tune's): what it reports right now (null where it doesn't), the same for every vendor
/// (<see cref="AmdAdlx.ReadSensors"/>, <see cref="NvidiaSensors.Read"/>).
/// </summary>
internal sealed record GpuSensors(double? TemperatureC, double? HotspotC, double? Watts, int? ClockMhz, int? VramUsedMb, int? VramTotalMb,
    int? FanRpm, double? UsagePercent, double? MemoryC = null, HeatLimit? Limit = null);

/// <summary>Where the card's own limits put warm and hot, on the reading they apply to: a Radeon's hotspot, a GeForce's core.</summary>
internal sealed record HeatLimit(bool OnHotspot, int WarnC, int BadC);
