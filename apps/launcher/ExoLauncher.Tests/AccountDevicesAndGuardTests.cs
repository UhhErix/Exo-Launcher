using System.Net;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

[Collection("AccountSessionStore")]
public sealed class AccountDevicesAndGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-device-guard-" + Guid.NewGuid().ToString("N"));

    public AccountDevicesAndGuardTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        AccountDevice.IdentityProvider = null;
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DeviceIdIsStableAcrossDifferentDataFoldersOnSamePc()
    {
        AccountDevice.IdentityProvider = () => ("11111111-2222-3333-4444-555555555555", "S-1-5-21-100-200-300-1001");
        var dirA = Directory.CreateDirectory(Path.Combine(_root, "folder-a")).FullName;
        var dirB = Directory.CreateDirectory(Path.Combine(_root, "folder-b")).FullName;

        var idA = AccountDevice.Id(dirA);
        var idB = AccountDevice.Id(dirB);

        Assert.True(AccountDevice.IsValid(idA));
        Assert.Equal(idA, idB);
        Assert.DoesNotContain("11111111", idA, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1001", idA, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePcIdChangesWhenMachineGuidOrUserSidChanges()
    {
        var baseId = AccountDevice.DerivePcId("11111111-2222-3333-4444-555555555555", "S-1-5-21-1-2-3-1001");
        var otherPc = AccountDevice.DerivePcId("99999999-2222-3333-4444-555555555555", "S-1-5-21-1-2-3-1001");
        var otherUser = AccountDevice.DerivePcId("11111111-2222-3333-4444-555555555555", "S-1-5-21-1-2-3-1002");

        Assert.True(AccountDevice.IsValid(baseId));
        Assert.NotEqual(baseId, otherPc);
        Assert.NotEqual(baseId, otherUser);
    }

    [Fact]
    public void LegacyDeviceIdFilesMapExistingSessionsOntoThisPcRow()
    {
        AccountDevice.IdentityProvider = () => ("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "S-1-5-21-9-8-7-1001");
        var localAppData = Directory.CreateDirectory(Path.Combine(_root, "local")).FullName;
        var launcherDir = Directory.CreateDirectory(Path.Combine(localAppData, "ExoLauncher")).FullName;
        var cinemaDir = Directory.CreateDirectory(Path.Combine(localAppData, "ExoCinema")).FullName;
        var browserDir = Directory.CreateDirectory(Path.Combine(localAppData, "ExoBrowser")).FullName;
        const string legacyLauncher = "b23185453b446a03b194f3bef56ae39c";
        const string legacyCinema = "6d9d01ab3d8c171722893bea3552bfc8";
        const string legacyBrowser = "fb5ab1bcd5e34d2201bf6d2470d592a1";
        const string otherPc = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        File.WriteAllText(Path.Combine(launcherDir, AccountDevice.FileName), legacyLauncher);
        File.WriteAllText(Path.Combine(cinemaDir, AccountDevice.FileName), legacyCinema);
        File.WriteAllText(Path.Combine(browserDir, AccountDevice.FileName), legacyBrowser);

        var stableId = AccountDevice.Id(launcherDir);
        var mine = AccountDevices.OfThisPc(stableId, launcherDir, localAppData);

        Assert.Contains(stableId, mine);
        Assert.Contains(legacyLauncher, mine);
        Assert.Contains(legacyCinema, mine);
        Assert.Contains(legacyBrowser, mine);

        var rows = new List<AccountSessionRow>
        {
            new(legacyBrowser, 100, 200, false, Name: "OLD-NAME"),
            new(legacyCinema, 150, 300, false, Name: "CINEMA-PC"),
            new(legacyLauncher, 160, 400, true, Name: "NEW-NAME"),
            new(otherPc, 120, 250, false, Name: "OTHER-OLD"),
        };

        var merged = AccountDevices.Merge(rows, stableId, mine);

        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].ThisPc);
        Assert.True(merged[0].Current);
        Assert.Equal(legacyLauncher, merged[0].Device);
        Assert.Equal(100, merged[0].Created);
        Assert.Equal(400, merged[0].LastUsed);
        Assert.Equal("NEW-NAME", merged[0].Name);
        Assert.False(merged[1].ThisPc);
        Assert.Equal(otherPc, merged[1].Device);
    }

    [Fact]
    public void MergeCollapsesDuplicateSessionsForSameOtherPcUsingNewestLastUsedAndName()
    {
        const string ownId = "11111111111111111111111111111111";
        const string laptop = "22222222222222222222222222222222";
        const string steamDeck = "33333333333333333333333333333333";

        var rows = new List<AccountSessionRow>
        {
            new(laptop, 200, 500, false, Name: "LAPTOP-OLD"),
            new(ownId, 100, 600, true, Name: "DESKTOP"),
            new(laptop, 300, 900, false, Name: "LAPTOP-NEW"),
            new(steamDeck, 400, 700, false),
        };

        var merged = AccountDevices.Merge(rows, ownId, new HashSet<string>(StringComparer.Ordinal) { ownId });

        Assert.Equal(3, merged.Count);
        Assert.True(merged[0].ThisPc);
        Assert.Equal(ownId, merged[0].Device);
        Assert.Equal(laptop, merged[1].Device);
        Assert.Equal(200, merged[1].Created);
        Assert.Equal(900, merged[1].LastUsed);
        Assert.Equal("LAPTOP-NEW", merged[1].Name);
        Assert.Equal(steamDeck, merged[2].Device);
    }

    [Fact]
    public async Task RevokeRefusesThisPcLegacyDeviceIdAndAllowsStaleOtherPc()
    {
        AccountDevice.IdentityProvider = () => ("12345678-1234-1234-1234-1234567890ab", "S-1-5-21-1-2-3-1001");
        var dataDir = Directory.CreateDirectory(Path.Combine(_root, "revoke-dir")).FullName;
        const string legacyId = "b23185453b446a03b194f3bef56ae39c";
        const string staleOther = "40d81395b032b9106b43e1c1633c38be";
        File.WriteAllText(Path.Combine(dataDir, AccountDevice.FileName), legacyId);
        AccountSessionStore.Save(dataDir, new AccountSession("tok", "acc-1", "person@example.com"));

        var refused = await ExoAccountSettings.RevokeAsync(dataDir, legacyId, new OkHandler());
        var allowed = await ExoAccountSettings.RevokeAsync(dataDir, staleOther, new OkHandler());

        Assert.False(refused.Ok);
        Assert.Contains("Sign out", refused.Message);
        Assert.True(allowed.Ok);
    }

    [Fact]
    public void RealServiceGuardBlocksTempDirDisposableHostAndTestRunWithoutMockHandler()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "exo-guard-check");
        var prodDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher");
        var devUri = new Uri("https://exo-account-dev.workers.dev/");

        Assert.True(ExoAccountClient.RefusesRealService(tempDir, ExoAccountClient.Service, hasCustomHandler: false, isDisposable: false, isTestRun: false));
        Assert.True(ExoAccountClient.RefusesRealService(prodDir, ExoAccountClient.Service, hasCustomHandler: false, isDisposable: true, isTestRun: false));
        Assert.True(ExoAccountClient.RefusesRealService(prodDir, ExoAccountClient.Service, hasCustomHandler: false, isDisposable: false, isTestRun: true));
        Assert.False(ExoAccountClient.RefusesRealService(prodDir, ExoAccountClient.Service, hasCustomHandler: false, isDisposable: false, isTestRun: false));
        Assert.False(ExoAccountClient.RefusesRealService(tempDir, ExoAccountClient.Service, hasCustomHandler: true, isDisposable: true, isTestRun: true));
        Assert.False(ExoAccountClient.RefusesRealService(tempDir, devUri, hasCustomHandler: false, isDisposable: true, isTestRun: true));
    }

    [Fact]
    public async Task ClientRefusesRealAccountServiceInTestOrDisposableHostBeforeSending()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(_root, "blocked-signin")).FullName;

        using var client = new ExoAccountClient(handler: null, service: ExoAccountClient.Service, dataDir: tempDir);
        var reply = await client.SignInAsync("person@example.com", "correct horse battery", AccountDevice.Id(tempDir));
        var outcome = ExoAccountOutcome.From(reply);

        Assert.False(reply.Ok);
        Assert.Equal(ExoAccountClient.TestHostRefusedError, reply.Error);
        Assert.False(outcome.Ok);
        Assert.False(outcome.Offline);
        Assert.Equal(HostMode.Refusal, outcome.Message);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("", Encoding.UTF8, "application/json") });
    }
}
