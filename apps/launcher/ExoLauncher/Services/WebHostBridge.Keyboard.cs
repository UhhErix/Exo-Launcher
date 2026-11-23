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
        var rgbBrightness = ReadDouble(p, hasParams, "rgbBrightness") is { } rawRgb && double.IsFinite(rawRgb)
            ? (int?)Math.Round(rawRgb)
            : null;
        return FormatKeyboardRead(WootingKeyboard.UpdateProfile(card, actuation, rt, rtMm, tachyon, rgbBrightness));
    }

    private static object KeyboardOpenApp() =>
        new { ok = WootingKeyboard.LaunchWootility() };

    private object KeyboardBackup(JsonElement p, bool hasParams)
    {
        if (FindWootingKeyboard(p, hasParams) is not { } card)
            return new { ok = false, message = "Wooting keyboard not connected.", backups = Array.Empty<object>() };
        var res = WootingKeyboard.Backup(card);
        return new
        {
            ok = res.Ok,
            message = res.Message,
            created = res.Created is null ? null : FormatBackupSummary(res.Created),
            backups = res.Backups.Select(FormatBackupSummary).ToList(),
        };
    }

    private object KeyboardBackups(JsonElement p, bool hasParams)
    {
        var card = FindWootingKeyboard(p, hasParams);
        var backups = WootingKeyboard.ListBackups(card);
        return new { ok = true, backups = backups.Select(FormatBackupSummary).ToList() };
    }

    private object KeyboardRestore(JsonElement p, bool hasParams)
    {
        if (FindWootingKeyboard(p, hasParams) is not { } card)
            return new { ok = false, message = "Wooting keyboard not connected." };
        var fileName = ReadString(p, hasParams, "fileName");
        if (string.IsNullOrWhiteSpace(fileName))
            return new { ok = false, message = "Missing backup file name." };
        return FormatKeyboardRead(WootingKeyboard.RestoreBackup(card, fileName));
    }

    private static object FormatBackupSummary(WootingKeyboard.KeyboardBackupSummary b) => new
    {
        fileName = b.FileName,
        model = b.Model,
        productId = b.ProductId,
        firmware = b.Firmware,
        createdUtc = b.CreatedUtc,
        profileCount = b.ProfileCount,
    };

    private static object FormatKeyboardRead(WootingKeyboard.KeyboardRead r) => new
    {
        ok = r.Ok,
        message = r.Message,
        model = r.Model,
        formFactor = r.FormFactor,
        hallEffect = r.HallEffect,
        hasRapidTrigger = r.HasRapidTrigger,
        hasRgb = r.HasRgb,
        maxScanHz = r.MaxScanHz,
        minActuationMm = r.MinActuationMm,
        maxActuationMm = r.MaxActuationMm,
        firmware = r.Firmware,
        layout = r.Layout,
        layoutCode = r.LayoutCode,
        activeProfile = r.ActiveProfile,
        profiles = r.Profiles.Select(p => new { index = p.Index, name = p.Name }).ToList(),
        actuationMm = r.ActuationMm,
        rapidTrigger = r.RapidTrigger,
        rapidTriggerMm = r.RapidTriggerMm,
        tachyon = r.Tachyon,
        rgbBrightness = r.RgbBrightness,
        canWrite = r.CanWrite,
        wootilityInstalled = r.WootilityInstalled,
        backgroundServiceRunning = r.BackgroundServiceRunning,
    };
}
