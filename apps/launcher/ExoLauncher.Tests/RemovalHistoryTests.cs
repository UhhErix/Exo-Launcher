using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Put back, against a made-up Recycle Bin in a temp folder (the bin's own $I and $R files, as Windows writes them).
/// Ways it could fail: a $I record is misread and a file is put somewhere else; something that is there again is overwritten;
/// an emptied bin reads as success, or Put back offers what can't come back; a .reg changed after it was written is imported
/// by an administrator; one elevated prompt per item, or anything run elevated when Windows' prompt was declined; a removal
/// and the clear after it become two records; a declined Put back can't be tried again.
/// </summary>
public sealed class RemovalHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-removals-" + Guid.NewGuid().ToString("N"));
    private string Bin => Path.Combine(_root, "bin");
    private static readonly LeftoverSummary Some = new(1, 0, 1, 0, 10);

    public RemovalHistoryTests() => Directory.CreateDirectory(Bin);

    private RemovalHistory History() => new(Path.Combine(_root, "removals.json"), _ => [Bin]);

    private static byte[] Info(string path, DateTime at, int version = 2)
    {
        var words = Encoding.Unicode.GetBytes(path + '\0');
        using var data = new MemoryStream();
        data.Write(BitConverter.GetBytes((long)version));
        data.Write(BitConverter.GetBytes(10L));
        data.Write(BitConverter.GetBytes(at.ToFileTimeUtc()));
        if (version == 2) data.Write(BitConverter.GetBytes(path.Length + 1));
        data.Write(words);
        if (version == 1) data.Write(new byte[520 - words.Length]);
        return data.ToArray();
    }

    // Something in the bin, as Windows leaves it: $I with where it came from, $R with what it was.
    private string Binned(string original, string suffix, bool folder = false, DateTime? at = null)
    {
        File.WriteAllBytes(Path.Combine(Bin, "$I" + suffix), Info(original, at ?? DateTime.UtcNow));
        var held = Path.Combine(Bin, "$R" + suffix);
        if (folder)
        {
            Directory.CreateDirectory(held);
            File.WriteAllText(Path.Combine(held, "inside.txt"), "x");
        }
        else File.WriteAllText(held, "x");
        return held;
    }

    private string Original(string name) => Path.Combine(_root, "was", name);

    private static Task<bool> NoAdmin(IReadOnlyList<string> steps) => throw new InvalidOperationException("nothing here needs an administrator");

    [Fact]
    public void BothOfWindowsRecordFormatsAreRead()
    {
        var at = new DateTime(2026, 10, 3, 4, 5, 6, DateTimeKind.Utc);
        Assert.Equal((@"C:\Program Files\Acme", at), RemovalHistory.ReadInfo(Info(@"C:\Program Files\Acme", at, 2)));
        Assert.Equal((@"C:\Program Files\Acme", at), RemovalHistory.ReadInfo(Info(@"C:\Program Files\Acme", at, 1)));
        Assert.Null(RemovalHistory.ReadInfo([1, 2, 3]));
        Assert.Null(RemovalHistory.ReadInfo(Info("x", at, 9)));
        var lying = Info(@"C:\a", at);
        BitConverter.TryWriteBytes(lying.AsSpan(24), 99999);
        Assert.Null(RemovalHistory.ReadInfo(lying));
    }

    [Fact]
    public async Task AFileAndAFolderComeBackWhereTheyWereAndLeaveTheBin()
    {
        var file = Original(@"deep\a.txt");
        var folder = Original("Acme");
        Binned(file, "AAA");
        Binned(folder, "BBB", folder: true);
        var history = History();
        var id = history.Add("Acme", Some, [new(file, false), new(folder, false)], [], [], DateTime.UtcNow);

        var done = await history.UndoAsync(id, _ => true, NoAdmin);

        Assert.Equal(new PutBack(2, 0, false), done);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(folder, "inside.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Bin));
        Assert.True(history.All().Single().Undone);
    }

    [Fact]
    public async Task WhatIsThereAgainIsNeverOverwrittenAndWhatLeftTheBinIsSaid()
    {
        var there = Original("there.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(there)!);
        File.WriteAllText(there, "new");
        var held = Binned(there, "AAA");
        var history = History();
        var id = history.Add("Acme", Some, [new(there, false), new(Original("emptied.txt"), false)], [], [], DateTime.UtcNow);

        var done = await history.UndoAsync(id, _ => true, NoAdmin);

        Assert.Equal(new PutBack(0, 2, false), done);
        Assert.Equal("new", File.ReadAllText(there));
        Assert.True(File.Exists(held));
    }

    [Fact]
    public void ThePageIsOnlyOfferedPutBackWhenSomethingCanComeBack()
    {
        var history = History();
        var file = Original("a.txt");
        history.Add("Gone", Some, [new(file, false)], [], [], DateTime.UtcNow);
        var record = history.All().Single();

        Assert.False(history.CanPutBack(record, history.Bin([file])));
        Binned(file, "AAA");
        Assert.True(history.CanPutBack(record, history.Bin([file])));
        Assert.False(history.CanPutBack(record with { Undone = true }, history.Bin([file])));
    }

    [Fact]
    public void TheNewestOfTheSamePathInTheBinIsTheOneThatComesBack()
    {
        var file = Original("a.txt");
        Binned(file, "OLD", at: DateTime.UtcNow.AddDays(-3));
        Binned(file, "NEW", at: DateTime.UtcNow);

        Assert.EndsWith("$RNEW", History().Bin([file])[file].Held);
    }

    [Fact]
    public async Task WhatNeedsAnAdministratorIsOnePromptAndDecliningItChangesNothing()
    {
        var one = Original("one");
        var two = Original("two");
        Binned(one, "AAA", folder: true);
        Binned(two, "BBB", folder: true);
        var reg = Path.Combine(_root, "acme.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Acme]\r\n");
        var history = History();
        var id = history.Add("Acme", Some, [new(one, true), new(two, true)], [reg], [@"HKLM\SOFTWARE\Acme"], DateTime.UtcNow);
        var prompts = new List<IReadOnlyList<string>>();

        var declined = await history.UndoAsync(id, _ => throw new InvalidOperationException("HKLM's isn't this user's to import"), steps => { prompts.Add(steps); return Task.FromResult(false); });

        Assert.True(declined!.Declined);
        var prompt = Assert.Single(prompts);
        Assert.Contains(prompt, step => step.StartsWith("move /y ", StringComparison.Ordinal) && step.EndsWith($"\"{one}\"", StringComparison.Ordinal));
        Assert.Contains($"reg import \"{reg}\"", prompt);
        Assert.False(history.All().Single().Undone);   // it can be tried again
    }

    [Fact]
    public async Task AUsersOwnRegistryFileIsImportedWithoutAPrompt()
    {
        var reg = Path.Combine(_root, "acme.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Acme]\r\n");
        var history = History();
        var id = history.Add("Acme", Some, [], [reg], [@"HKCU\Software\Acme"], DateTime.UtcNow);
        var imported = new List<string>();

        var done = await history.UndoAsync(id, file => { imported.Add(file); return true; }, NoAdmin);

        Assert.Equal([reg], imported);
        Assert.Equal(new PutBack(1, 0, false), done);
    }

    [Theory]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Acme]", true)]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Acme\\Sub]", true)]
    [InlineData("[hkey_local_machine\\software\\acme]", true)]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Acme Other]", false)]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run]", false)]
    [InlineData("[-HKEY_LOCAL_MACHINE\\SOFTWARE\\Acme]", false)]
    [InlineData("[HKEY_CURRENT_USER\\Software\\Acme]", false)]
    public void ASavedRegMayOnlyNameWhatWasRemoved(string line, bool stays) =>
        Assert.Equal(stays, RemovalHistory.RegStaysInside($"Windows Registry Editor Version 5.00\r\n\r\n{line}\r\n\"a\"=\"[not a key]\"\r\n", [@"HKLM\SOFTWARE\Acme"]));

    [Fact]
    public async Task ARegChangedSinceItWasWrittenIsRefused()
    {
        var reg = Path.Combine(_root, "acme.reg");
        File.WriteAllText(reg, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"x\"=\"evil.exe\"\r\n");
        var history = History();
        var id = history.Add("Acme", Some, [], [reg], [@"HKLM\SOFTWARE\Acme"], DateTime.UtcNow);

        var done = await history.UndoAsync(id, _ => throw new InvalidOperationException(), NoAdmin);

        Assert.Equal(new PutBack(0, 1, false), done);
    }

    [Fact]
    public void AnUninstallAndItsClearAreOneRecordAndAWeekLaterIsAnother()
    {
        var history = History();
        var now = DateTime.UtcNow;
        var first = history.Add("Acme", new(0, 0, 0, 0, 0), [], [], [], now);
        var second = history.Add("Acme", Some, [new(Original("a"), false)], ["a.reg"], [@"HKCU\Software\Acme"], now.AddMinutes(5));
        var third = history.Add("Acme", Some, [], [], [], now.AddDays(7));

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        var kept = history.All();
        Assert.Equal(2, kept.Count);
        Assert.Equal(third, kept[0].Id);
        Assert.Equal(Some, kept[1].Cleared);
        Assert.Single(kept[1].Files);
    }

    [Fact]
    public async Task WhatNoLongerExistsOrIsDoneIsNotUndone()
    {
        var history = History();
        Assert.Null(await history.UndoAsync("nothing", _ => true, NoAdmin));
        var id = history.Add("Acme", Some, [], [], [], DateTime.UtcNow);
        await history.UndoAsync(id, _ => true, NoAdmin);
        Assert.Null(await history.UndoAsync(id, _ => true, NoAdmin));
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
    }
}
