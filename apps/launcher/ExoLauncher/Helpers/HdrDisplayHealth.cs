namespace ExoLauncher.Helpers;

internal static class HdrDisplayHealth
{
    internal const double GapWarnThresholdPercent = 10.0;

    internal sealed record PeakHealth(string Status, int? EdidNits, int? DxgiNits, double? GapPercent, string? Warning);

    internal static PeakHealth Evaluate(bool isInternal, int? edidPeakNits, int? dxgiPeakNits)
    {
        if (edidPeakNits is not > 0 || dxgiPeakNits is not > 0)
            return new PeakHealth("unknown", edidPeakNits is > 0 ? edidPeakNits : null, dxgiPeakNits is > 0 ? dxgiPeakNits : null, null, null);

        var gap = Math.Round(Math.Abs(dxgiPeakNits.Value - edidPeakNits.Value) / (double)edidPeakNits.Value * 100.0, 1);
        if (gap <= GapWarnThresholdPercent)
            return new PeakHealth("ok", edidPeakNits.Value, dxgiPeakNits.Value, gap, null);

        if (isInternal)
        {
            var warning = $"Your laptop HDR sliders report {dxgiPeakNits.Value} nits to games instead of your panel's true {edidPeakNits.Value} nits — highlights will clip or look dim.";
            return new PeakHealth("mismatch", edidPeakNits.Value, dxgiPeakNits.Value, gap, warning);
        }

        return new PeakHealth("reported", edidPeakNits.Value, dxgiPeakNits.Value, gap, null);
    }

    internal static bool IsCursorFixEligible(bool isInternal, bool hdrSupported, double? sdrWhiteNits, bool alreadyEnabled)
    {
        if (isInternal || !hdrSupported) return false;
        if (alreadyEnabled) return true;
        return sdrWhiteNits is > 0 and < HdrBrightness.CursorFixThresholdNits;
    }

    internal static bool ShouldApplyCursorFix(bool isInternal, bool hdrSupported, bool optIn, double? sdrWhiteNits) =>
        !isInternal && hdrSupported && optIn && sdrWhiteNits is > 0 and < HdrBrightness.CursorFixThresholdNits;
}
