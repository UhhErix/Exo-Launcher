using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What happens around an update as Exo starts and as a game ends. Ways it could fail:
/// - a game ending is announced (LeanSignal.Changed) while GameRunningSignal still says a game runs, so an update kept for
///   after the game never becomes ready;
/// - a launch while Setup swaps the app starts the old build in the middle of the swap, or the update's own starts (the
///   health check, the rollback, the last open) are held back too, so the update rolls back or Exo never comes back;
/// - a note or an exolauncher:// link a launch was for is lost instead of reaching the new build, or an old one is taken.
/// </summary>
[Collection(ProcessWideSignals.Name)]
public sealed class UpdateStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-update-start-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void AGameEndingIsSeenByWhatListensToTheLeanSignal()
    {
        // The real signal and the real lean signal; only Wi-Fi and the named event Exo shares with other copies stay out.
        var seen = new List<bool>();
        void Listen() => seen.Add(GameRunningSignal.Running);
        LeanSignal.Changed += Listen;
        try
        {
            GameRunningSignal.Update(true, GameRunningSignal.Generation, _ => { }, LeanSignal.Update, _ => { });
            GameRunningSignal.Update(false, GameRunningSignal.Generation, _ => { }, LeanSignal.Update, _ => { });
        }
        finally { LeanSignal.Changed -= Listen; }
        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void AnUpdateWaitingForTheGameIsReadyWhenTheRealSignalSaysTheGameEnded()
    {
        var service = new AppUpdateService(NoNetwork(), _root, TimeProvider.System) { GameRunning = () => GameRunningSignal.Running };
        void Changed() => service.GameChanged();
        LeanSignal.Changed += Changed;
        try
        {
            GameRunningSignal.Update(true, GameRunningSignal.Generation, _ => { }, LeanSignal.Update, _ => { });
            Assert.Equal(UpdateFlow.State.Downloading, service.Move(UpdateFlow.Step.Install));
            Assert.Equal(UpdateFlow.State.WaitingForGame, service.Move(UpdateFlow.Step.Verified));
            GameRunningSignal.Update(false, GameRunningSignal.Generation, _ => { }, LeanSignal.Update, _ => { });
            Assert.Equal(UpdateFlow.State.Ready, service.State);
        }
        finally
        {
            LeanSignal.Changed -= Changed;
            GameRunningSignal.Update(false, GameRunningSignal.Generation, _ => { }, LeanSignal.Update, _ => { });
        }
    }

    private static HttpClient NoNetwork()
    {
#pragma warning disable RS0030 // No request is made in these tests.
        return new HttpClient(new Refuse());
#pragma warning restore RS0030
    }

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    // ---- a launch during an update (UpdateHandOff) ----

    private string Updates => Path.Combine(_root, "updates");
    private string SetupImage => Path.Combine(Updates, "ExoLauncher-Setup-" + Guid.NewGuid().ToString("N") + ".exe");

    [Fact]
    public void ALaunchDuringTheSwapIsHeldBackButTheUpdatesOwnStartsAreNot()
    {
        string?[] running = [@"C:\Windows\explorer.exe", SetupImage];
        Assert.True(UpdateHandOff.HoldsBack(running, false, Updates, healthNonce: null, fromSetup: null));
        // The new build's health check, the rollback's restart and Setup's last open.
        Assert.False(UpdateHandOff.HoldsBack(running, false, Updates, healthNonce: new string('a', 32), fromSetup: null));
        Assert.False(UpdateHandOff.HoldsBack(running, false, Updates, healthNonce: null, fromSetup: "1"));
    }

    [Fact]
    public void OnlyExosOwnSetupFromTheUpdatesFolderHoldsALaunchBack()
    {
        Assert.False(UpdateHandOff.HoldsBack([@"C:\Windows\explorer.exe", null], false, Updates, null, null));
        // A Setup downloaded from the website (Downloads) is a first install or a repair, not an in-app update.
        Assert.False(UpdateHandOff.HoldsBack([@"C:\Users\x\Downloads\ExoLauncher-Setup-0123456789abcdef0123456789abcdef.exe"], false, Updates, null, null));
        Assert.False(UpdateHandOff.HoldsBack([Path.Combine(Updates, "ExoLauncher-Setup.partial")], false, Updates, null, null));
    }

    [Fact]
    public void ALinkOrANoteALaunchWasForReachesTheNextStart()
    {
        var file = Path.Combine(_root, "launch-handoff.json");
        var now = DateTimeOffset.UtcNow;
        string[] link = ["exolauncher://library"];
        Assert.True(UpdateHandOff.Save(file, link, now));
        Assert.Equal(link, UpdateHandOff.Take(file, [], now.AddMinutes(1)));
        Assert.False(File.Exists(file));
        // Taken once only.
        Assert.Empty(UpdateHandOff.Take(file, [], now.AddMinutes(1)));
    }

    [Fact]
    public void NothingToOpenIsNotKeptAndAnOldOrOverriddenOneIsNotTaken()
    {
        var file = Path.Combine(_root, "launch-handoff.json");
        var now = DateTimeOffset.UtcNow;
        Assert.False(UpdateHandOff.Save(file, [], now));
        Assert.False(File.Exists(file));
        UpdateHandOff.Save(file, ["exolauncher://library"], now);
        Assert.Empty(UpdateHandOff.Take(file, [], now.AddHours(1)));
        UpdateHandOff.Save(file, ["exolauncher://library"], now);
        string[] own = ["exolauncher://pc"];
        Assert.Equal(own, UpdateHandOff.Take(file, own, now));
    }

    [Fact]
    public void ADoubleClickWithExoRunningGoesToItEvenWhileAnUpdateRuns()
    {
        var asked = 0;
        Assert.Equal(UpdateHandOff.Route.ToRunningExo, UpdateHandOff.RouteOf(anotherExoRuns: true, () => { asked++; return true; }));
        Assert.Equal(0, asked);
        Assert.Equal(UpdateHandOff.Route.WaitForUpdate, UpdateHandOff.RouteOf(anotherExoRuns: false, () => true));
        Assert.Equal(UpdateHandOff.Route.Start, UpdateHandOff.RouteOf(anotherExoRuns: false, () => false));
    }

    [Fact]
    public void TheInstallEnginesOwnMarkHoldsALaunchBackWithoutSetupsWindow()
    {
        Assert.True(UpdateHandOff.HoldsBack([@"C:\Windows\explorer.exe"], engineRunning: true, Updates, null, null));
        Assert.False(UpdateHandOff.HoldsBack([], engineRunning: true, Updates, healthNonce: new string('a', 32), fromSetup: null));
        Assert.False(UpdateHandOff.HoldsBack([], engineRunning: true, Updates, null, fromSetup: "1"));
    }

    [Fact]
    public void TheEnginesMarkIsSeenWhileHeldAndGoneOnceReleased()
    {
        // The real named mutex the engine creates (a test-only name, so a real update on this PC is never imitated).
        var name = @"Local\ExoLauncher.Installing.test-" + Guid.NewGuid().ToString("N");
        Assert.False(UpdateHandOff.EngineRunning(name));
        using (new Mutex(false, name)) Assert.True(UpdateHandOff.EngineRunning(name));
        Assert.False(UpdateHandOff.EngineRunning(name));
    }
}

/// <summary>Tests that set Exo's process-wide game signals (GameRunningSignal, LeanSignal) run alone, never beside another test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideSignals
{
    public const string Name = "Process-wide game signals";
}