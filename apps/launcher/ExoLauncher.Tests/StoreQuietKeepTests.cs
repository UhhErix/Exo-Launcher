using ExoLauncher.Adapters;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Steam's quiet values keep what was there (StoreQuietPass, with Keep's record in a temp folder; Steam's files are the
/// fixtures and temp copies, never this PC's). Ways it could fail: a value overwritten with nothing recorded of it, or
/// recorded after the write; a second write recording Exo's own value as "before"; a write Steam did not take reported as
/// taken; a value Steam changed back left as it is; Off that leaves a key, a section Exo added or a byte changed
/// elsewhere, or forgets what it did not put back; quiet written while it is off.
/// </summary>
public sealed partial class StoreQuietTests
{
    private sealed class Steam : IDisposable
    {
        private readonly IDisposable _keep = Keep.RecordForTest(out _);
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "exo-quiet-keep-" + Guid.NewGuid().ToString("N"));
        internal string LocalPath => Path.Combine(Root, "userdata", "123", "config", "localconfig.vdf");
        internal string SharedPath => Path.Combine(Root, "userdata", "123", "7", "remote", "sharedconfig.vdf");
        internal string LocalText { get; }
        internal string SharedText { get; }

        internal Steam(string local, string shared)
        {
            LocalText = local;
            SharedText = shared;
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(SharedPath)!);
            File.WriteAllText(LocalPath, local);
            File.WriteAllText(SharedPath, shared);
        }

        internal string Local => File.ReadAllText(LocalPath);
        internal string SharedNow => File.ReadAllText(SharedPath);

        public void Dispose()
        {
            _keep.Dispose();
            DeletionGate.DeleteDirectory(Root, recursive: true);
        }
    }

    private static Keep.KeepStore Account() => Keep.Load().Stores["steam/123"];

    [Fact]
    public void QuietKeepsWhatEachValueHeldBeforeItsFirstWrite()
    {
        using var steam = new Steam(Local, Shared);
        var (written, notTaken) = StoreQuietPass.Run(steam.Root, wanted: true);
        Assert.Equal(2, written);
        Assert.Empty(notTaken);
        var held = Account().Held;
        // A value that held something: its text as the file had it.
        Assert.Equal("1", held["local|UserLocalConfigStore/friends|Notifications_ShowIngame"].Raw);
        Assert.False(held["local|UserLocalConfigStore/friends|Notifications_ShowIngame"].Absent);
        Assert.Equal("0", held["shared|UserRoamingConfigStore|DisableAllToasts"].Raw);
        // The friends list's JSON as the file spells it, escapes kept.
        Assert.StartsWith("{\\\"bNotifications_ShowIngame\\\":true",
            held["shared|UserRoamingConfigStore/Software/Valve/Steam/FriendsUI|FriendsUIJSON"].Raw);
        // A value the file did not have: absent, with how much of its path the file had.
        var absent = held["shared|UserRoamingConfigStore|DisableToastsInGame"];
        Assert.True(absent.Absent);
        Assert.Equal(1, absent.Depth);
        var news = held["local|UserLocalConfigStore/news|NotifyAvailableGames"];
        Assert.True(news.Absent);
        Assert.Equal(1, news.Depth);
    }

    [Fact]
    public void AValueIsNotWrittenWhenWhatItHeldCannotBeRecorded()
    {
        using var steam = new Steam(Local, Shared);
        using (Keep.FailSavesForTest())
        {
            var (written, _) = StoreQuietPass.Run(steam.Root, wanted: true);
            Assert.Equal(0, written);
        }
        Assert.Equal(Local, steam.Local);
        Assert.Equal(Shared, steam.SharedNow);
    }

    [Fact]
    public void QuietIsReadBackAndAValueThatDidNotTakeIsNamed()
    {
        using var steam = new Steam(Local, Shared);
        // Steam puts one value back as the file is written (as a cloud round trip can).
        var (_, notTaken) = StoreQuietPass.Run(steam.Root, wanted: true, (path, text) =>
            File.WriteAllText(path, text.Replace("\"DisableAllToasts\"\t\t\"1\"", "\"DisableAllToasts\"\t\t\"0\"")));
        Assert.Equal(["DisableAllToasts"], notTaken.Distinct());
        // Read back from the disk: the others did take.
        Assert.Equal("0", SteamKeyValues.ParseText(steam.SharedNow).Section("UserRoamingConfigStore").Value("PlaySoundOnToast"));
    }

    [Fact]
    public void AValueSteamChangedBackIsWrittenAgain()
    {
        using var steam = new Steam(Local, Shared);
        StoreQuietPass.Run(steam.Root, wanted: true);
        Assert.Equal(0, StoreQuietPass.Pending(steam.Root, wanted: true));
        // An update (or Steam Cloud) hands the file back with a toast switch changed.
        File.WriteAllText(steam.SharedPath, steam.SharedNow.Replace("\"DisableAllToasts\"\t\t\"1\"", "\"DisableAllToasts\"\t\t\"2\""));
        Assert.Equal(1, StoreQuietPass.Pending(steam.Root, wanted: true));
        var (written, notTaken) = StoreQuietPass.Run(steam.Root, wanted: true);
        Assert.Equal(1, written);
        Assert.Empty(notTaken);
        Assert.Equal("1", SteamKeyValues.ParseText(steam.SharedNow).Section("UserRoamingConfigStore").Value("DisableAllToasts"));
        // What it held before the first write is still what Off returns, not Exo's own value or the one Steam put back.
        Assert.Equal("0", Account().Held["shared|UserRoamingConfigStore|DisableAllToasts"].Raw);
    }

    [Fact]
    public void OffPutsBackEveryValueExactlyAndRemovesWhatWasAbsent()
    {
        foreach (var local in new[] { Local, RealLocalConfig() })
        {
            using var steam = new Steam(local, Shared);
            StoreQuietPass.Run(steam.Root, wanted: true);
            Assert.NotEqual(steam.LocalText, steam.Local);
            Assert.NotEqual(steam.SharedText, steam.SharedNow);
            Assert.Equal(2, StoreQuietPass.Pending(steam.Root, wanted: false));
            var (written, notBack) = StoreQuietPass.Run(steam.Root, wanted: false);
            Assert.Equal(2, written);
            Assert.Empty(notBack);
            // Every byte as it was: values put back, keys and sections only Exo added gone.
            Assert.Equal(steam.LocalText, steam.Local);
            Assert.Equal(steam.SharedText, steam.SharedNow);
            // Forgotten once put back.
            Assert.DoesNotContain("steam/123", Keep.Load().Stores.Keys);
            Assert.Equal("off", StoreQuietPass.Word(steam.Root, wanted: false));
        }
    }

    [Fact]
    public void NothingIsWrittenWhileQuietIsOff()
    {
        using var steam = new Steam(Local, Shared);
        // Off from the start: nothing recorded, nothing to put back, nothing written.
        Assert.Equal(0, StoreQuietPass.Pending(steam.Root, wanted: false));
        Assert.Equal(0, StoreQuietPass.Run(steam.Root, wanted: false).Written);
        Assert.Empty(Keep.Load().Stores);
        Assert.Equal(Local, steam.Local);
        Assert.Equal(Shared, steam.SharedNow);
        // Quiet on, then off; a toast the player later switches on in Steam is theirs: the off pass leaves it.
        StoreQuietPass.Run(steam.Root, wanted: true);
        StoreQuietPass.Run(steam.Root, wanted: false);
        File.WriteAllText(steam.SharedPath, steam.SharedNow.Replace("\"DisableAllToasts\"\t\t\"0\"", "\"DisableAllToasts\"\t\t\"1\""));
        var loud = steam.SharedNow;
        Assert.Equal(0, StoreQuietPass.Run(steam.Root, wanted: false).Written);
        Assert.Equal(loud, steam.SharedNow);
    }

    [Fact]
    public void TheRestOfTheFileIsByteForByteTheSame()
    {
        using var steam = new Steam(RealLocalConfig(), Shared);
        StoreQuietPass.Run(steam.Root, wanted: true);
        // Steam writes something else in the file meanwhile (a setting of its own).
        File.WriteAllText(steam.SharedPath, steam.SharedNow.Replace("\"#app_games\"", "\"#app_news\""));
        StoreQuietPass.Run(steam.Root, wanted: false);
        Assert.Equal(steam.SharedText.Replace("\"#app_games\"", "\"#app_news\""), steam.SharedNow);
        Assert.Equal(steam.LocalText, steam.Local);
    }

    [Fact]
    public void TheQuietSwitchIsOnByDefault() => Assert.True(new ExoLauncher.Models.AppSettings().StoreClientsQuiet);
}
