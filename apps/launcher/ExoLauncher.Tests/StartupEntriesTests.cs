using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways PC › Apps' Startup and the automatic junk clean could go wrong silently:
/// - a startup command's program is misread (a quoted path, an unquoted one with spaces, an argument taken for it, a
///   value that isn't a command named as one), so the wrong entry is called missing or matched;
/// - the catalog explains a generic program name ("scheduler.exe") from another app, or misses Discord behind Update.exe;
/// - a Recycle Bin record's deletion time is misread, so a fresh item goes as month-old or a corrupt record as anything;
/// - the weekly kinds run every day, or never;
/// - a value name or path with quotes or backslashes breaks the .reg file Windows imports.
/// </summary>
public sealed class StartupEntriesTests
{
    [Theory]
    [InlineData("\"C:\\Users\\A\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart --minimized", "C:\\Users\\A\\AppData\\Roaming\\Spotify\\Spotify.exe")]
    [InlineData("C:\\Program Files (x86)\\Steam\\steam.exe -silent", "C:\\Program Files (x86)\\Steam\\steam.exe")]
    [InlineData("\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\"", "C:\\Program Files\\Riot Vanguard\\vgtray.exe")]
    [InlineData("765aa5ec-50a2-4916-aba3-8ef112d65372", null)]
    [InlineData("2", null)]
    [InlineData("", null)]
    public void ProgramOf_reads_the_program_a_command_starts(string command, string? program)
    {
        Assert.Equal(program, StartupEntries.ProgramOf(command));
    }

    [Fact]
    public void Catalog_scopes_generic_names_and_finds_the_app_behind_an_updater()
    {
        // Driver Booster's scheduler counts only under its own folder.
        Assert.Equal("IObit Driver Booster scheduler",
            StartupCatalog.Find("Driver Booster Scheduler", ["scheduler.exe"], @"C:\Program Files (x86)\IObit\Driver Booster\14.0.0\Scheduler.exe")?.Name);
        Assert.Null(StartupCatalog.Find("Some Scheduler", ["scheduler.exe"], @"C:\Program Files\Other\Scheduler.exe"));
        // Squirrel's Update.exe hands off to Discord.exe.
        Assert.Equal("discord.exe", StartupCatalog.Find("Discord", ["update.exe", "discord.exe"], @"C:\Users\A\AppData\Local\Discord\Update.exe")?.Match[0]);
        // A Run value that isn't a program, by its name.
        Assert.Equal("remove", StartupCatalog.Find("AF_uuid_4126040", [], null)?.Verdict);
        Assert.Equal("keep", StartupCatalog.Find("Riot Vanguard", ["vgtray.exe"], @"C:\Program Files\Riot Vanguard\vgtray.exe")?.Verdict);
    }

    [Fact]
    public void A_recycle_bin_record_says_when_its_item_went_in()
    {
        var at = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var record = new byte[40];
        BitConverter.GetBytes(2L).CopyTo(record, 0);
        BitConverter.GetBytes(12345L).CopyTo(record, 8);
        BitConverter.GetBytes(at.ToFileTimeUtc()).CopyTo(record, 16);
        Assert.Equal(at, JunkFiles.DeletedAt(record));
        // A version Windows never wrote, a record cut short, a time that isn't one.
        BitConverter.GetBytes(7L).CopyTo(record, 0);
        Assert.Null(JunkFiles.DeletedAt(record));
        Assert.Null(JunkFiles.DeletedAt(new byte[16]));
        BitConverter.GetBytes(1L).CopyTo(record, 0);
        BitConverter.GetBytes(-5L).CopyTo(record, 16);
        Assert.Null(JunkFiles.DeletedAt(record));
    }

    [Fact]
    public void The_weekly_kinds_go_once_a_week()
    {
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(JunkAutoClean.WeekDue(null, now));
        Assert.False(JunkAutoClean.WeekDue(now.AddDays(-3), now));
        Assert.True(JunkAutoClean.WeekDue(now.AddDays(-7), now));
        Assert.True(JunkAutoClean.WeekDue(now.AddDays(2), now));
        Assert.False(JunkAutoClean.Auto("shaders"));
        Assert.True(JunkAutoClean.Auto("bin-old"));
        Assert.False(JunkAutoClean.Auto("bin"));
    }

    [Fact]
    public void A_reg_file_keeps_quotes_and_backslashes_whole()
    {
        Assert.Equal("Say \\\"hi\\\" C:\\\\Tools", StartupEntries.RegText("Say \"hi\" C:\\Tools"));
        Assert.Equal("hex:03,00,0a,ff", StartupEntries.RegHex([3, 0, 10, 255]));
    }
}
