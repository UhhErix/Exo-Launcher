using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private Peripherals.Peripheral? FindWootingKeyboard(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id");
        var list = Peripherals.List(() => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0);
        return list.FirstOrDefault(c => WootingKeyboard.IsSupported(c) && (string.IsNullOrEmpty(id) || c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
    }

    private object KeyboardRead(JsonElement p, bool hasParams)
    {
        if (FindWootingKeyboard(p, hasParams) is not { } card)
            return new { ok = false, message = "Wooting keyboard not connected." };
        return FormatKeyboardRead(WootingKeyboard.Read(card));
    }

    private object KeyboardProfile(JsonElement p, bool hasParams)
    {
        if (FindWootingKeyboard(p, hasParams) is not { } card)
            return new { ok = false, message = "Wooting keyboard not connected." };
        if (ReadDouble(p, hasParams, "slot") is not { } rawSlot || !double.IsFinite(rawSlot))
            return new { ok = false, message = "Missing profile slot." };
        return FormatKeyboardRead(WootingKeyboard.SwitchProfile(card, (int)Math.Round(rawSlot)));
    }

    private object KeyboardSettings(JsonElement p, bool hasParams)
    {
        if (FindWootingKeyboard(p, hasParams) is not { } card)
            return new { ok = false, message = "Wooting keyboard not connected." };
        var actuation = ReadDouble(p, hasParams, "actuationMm");
        var rt = ReadBool(p, hasParams, "rapidTrigger");
        var rtMm = ReadDouble(p, hasParams, "rapidTriggerMm");
        var tachyon = ReadBool(p, hasParams, "tachyon");
        return FormatKeyboardRead(WootingKeyboard.UpdateProfile(card, actuation, rt, rtMm, tachyon));
    }

    private static object KeyboardOpenApp() =>
        new { ok = WootingKeyboard.LaunchWootility() };

    private static object FormatKeyboardRead(WootingKeyboard.KeyboardRead r) => new
    {
        ok = r.Ok,
        message = r.Message,
        model = r.Model,
        hallEffect = r.HallEffect,
        maxScanHz = r.MaxScanHz,
        firmware = r.Firmware,
        layout = r.Layout,
        activeProfile = r.ActiveProfile,
        profiles = r.Profiles.Select(p => new { index = p.Index, name = p.Name }).ToList(),
        actuationMm = r.ActuationMm,
        rapidTrigger = r.RapidTrigger,
        rapidTriggerMm = r.RapidTriggerMm,
        tachyon = r.Tachyon,
        canWrite = r.CanWrite,
        wootilityInstalled = r.WootilityInstalled,
        backgroundServiceRunning = r.BackgroundServiceRunning,
    };
}
