using System.Globalization;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>Windows mouse parameters (`SPI_GETMOUSE` / `SPI_SETMOUSE`) behind one seam for unit testing.</summary>
internal interface IMouseAccelSystem
{
    int[]? Read();
    bool Write(int[] values);
}

internal sealed class NativeMouseAccelSystem : IMouseAccelSystem
{
    private const uint SpiGetMouse = 0x0003;
    private const uint SpiSetMouse = 0x0004;
    private const uint SpiFUpdateIniFileAndSendChange = 0x0003;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, [In, Out] int[] pvParam, uint fWinIni);

    public int[]? Read()
    {
        var values = new int[3];
        return SystemParametersInfo(SpiGetMouse, 0, values, 0) ? values : null;
    }

    public bool Write(int[] values)
    {
        if (HostMode.IsDisposable || values.Length != 3) return false;
        return SystemParametersInfo(SpiSetMouse, 0, values, SpiFUpdateIniFileAndSendChange);
    }
}

/// <summary>
/// Autopilot's automatic 1:1 mouse movement: turns Windows' "Enhance pointer precision" off (`[0, 0, 0]`) once on startup
/// without UAC elevation, recording the exact prior values in <see cref="AutopilotJournal"/> (`Held: true`) so an uninstall
/// puts them back. If the user manually turns pointer precision back on in Windows later, Exo respects their choice and never
/// fights them once already held in the journal.
/// </summary>
internal sealed class AutopilotMouseAccel(IMouseAccelSystem system, AutopilotJournal journal, Action<string>? logLine = null)
{
    internal const string Kind = "MouseAccel";
    private readonly Action<string> log = logLine ?? AppLog.Info;

    internal JournalEntry? Held => journal.Entries.FirstOrDefault(entry => entry.Kind == Kind);

    internal bool IsOff() => system.Read() is { Length: 3 } mp && mp[2] == 0;

    internal string? Apply()
    {
        if (Held is not null) return null;
        int[]? current;
        try { current = system.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { current = null; }
        if (current is not { Length: 3 }) return "Windows didn't say what mouse acceleration is set to.";
        if (current[2] == 0) return null;

        var note = string.Create(CultureInfo.InvariantCulture, $"{current[0]},{current[1]},{current[2]}");
        var entry = new JournalEntry(Kind, 0, 0, "Mouse acceleration", current[2], 0, Held: true, Note: note);
        if (!journal.Add(entry)) return "Exo couldn't save what mouse acceleration was set to, so nothing changed.";

        bool wrote;
        try
        {
            wrote = system.Write([0, 0, 0]) && system.Read() is { Length: 3 } after && after[2] == 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { wrote = false; }

        if (wrote)
        {
            log($"Autopilot: mouse acceleration off (was {note}).");
            return null;
        }

        journal.Remove(entry);
        return "Windows didn't turn mouse acceleration off.";
    }

    internal string? PutBack()
    {
        if (Held is not { } held) return null;
        int[]? now = null;
        try { now = system.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        if (now is [0, 0, 0] && ParseNote(held.Note) is { Length: 3 } restore)
        {
            bool wrote;
            try { wrote = system.Write(restore); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { wrote = false; }
            if (!wrote) return "Windows didn't put mouse acceleration back.";
        }

        journal.Remove(held);
        log("Autopilot: mouse acceleration put back.");
        return null;
    }

    private static int[]? ParseNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var parts = note.Split(',');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p0)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p1)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p2))
            return null;
        return [p0, p1, p2];
    }
}
