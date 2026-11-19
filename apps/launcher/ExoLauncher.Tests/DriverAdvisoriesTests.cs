using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the driver advisories could go wrong, each silent in the app:
/// - an entry in the built-in table that doesn't read (a bad pattern, no source, no date) silently drops out, or one has no source;
/// - a range read one off: the first affected version let through, or the fixed one still held back; a version that isn't
///   one (unreadable, another firmware line) matched anyway;
/// - a condition ignored: a game's advice on a PC without the game, a 24H2 firmware fault on 23H2, a chip revision Intel fixed;
/// - one provider's subject built differently from the patterns (network vs Windows Update rows of the same chip);
/// - a feed that isn't Exo's (tampered, another key, a malleated high-s signature, garbage) taken, a valid one refused
///   (node's signer and .NET's verifier disagreeing), an older one replayed over a newer, or a feed older than the
///   built-in table winning over it;
/// - an install path that installs a version the advisories hold back because it checked only when the list was read.
/// </summary>
public sealed class DriverAdvisoriesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-advisories-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) DeletionGate.DeleteDirectory(_folder, recursive: true);
    }

    private static readonly DriverAdvisories.Conditions Win11 = new([], 26300);
    private static readonly DriverAdvisories.Conditions WithPbz = new(["PHANTOM BLADE ZERO"], 26300);

    private static DriverAdvisories.Advisory? BuiltIn(string subject, string version, DriverAdvisories.Conditions? now = null) =>
        DriverAdvisories.Find(DriverAdvisories.BuiltIn.Advisories, subject, version, now ?? Win11);

    // ---- The built-in table ----

    [Fact]
    public void EveryBuiltInEntryReadsWithItsSource()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Repo.PathOf("ExoLauncher", "Services", "DriverAdvisories.json")));
        var written = doc.RootElement.GetProperty("advisories").GetArrayLength();
        var table = DriverAdvisories.BuiltIn;
        Assert.Equal(written, table.Advisories.Count); // none dropped for a bad pattern, source or date
        Assert.Equal(table.Advisories.Count, table.Advisories.Select(a => a.Id).Distinct().Count());
        Assert.All(table.Advisories, a =>
        {
            Assert.StartsWith("https://", a.Url, StringComparison.Ordinal);
            Assert.Matches(@"^\d{4}-\d{2}(-\d{2})?$", a.Date);
            Assert.False(string.IsNullOrWhiteSpace(a.Why));
            Assert.True(a.Do == DriverAdvisories.Advice.Skip || a.To is not null, a.Id);
        });
    }

    [Theory]
    // This PC: ASUS ROG STRIX B550-F GAMING WIFI II on BIOS 3636, without AGESA 1.2.0.12's fTPM fixes (AMD-SB-7064).
    [InlineData("bios:ASUS ROG STRIX B550-F GAMING WIFI II", "3636", "asus-b550f-wifi-ii-ftpm-2026")]
    [InlineData("bios:ASUS ROG STRIX B550-F GAMING WIFI II", "3644", null)]
    [InlineData("bios:ASUS ROG STRIX B550-F GAMING WIFI II", "3645", null)]
    [InlineData("bios:ASUS ROG STRIX B550-F GAMING", "3636", null)] // another board numbers its own BIOSes
    // This PC's RTX 3070 on 617.42, its I226-V on 2.1.5.12, its 990 PRO on 8B2QJXD7, its chipset on 8.09.28.1124: nothing.
    [InlineData("gpu:nvidia NVIDIA GeForce RTX 3070", "617.42", null)]
    [InlineData("network:PCI\\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\\6&102E3ADF&0&0048020A Intel(R) Ethernet Controller I226-V", "2.1.5.12", null)]
    [InlineData("ssd:Samsung SSD 990 PRO with Heatsink 2TB", "8B2QJXD7", null)]
    [InlineData("chipset:amd AMD Ryzen 5 5600X 6-Core Processor", "8.09.28.1124", null)]
    // The same chips on their known-bad versions.
    [InlineData("network:PCI\\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\\6&1 Intel(R) Ethernet Controller I226-V", "2.1.3.14", "intel-i226v-link-drops")]
    [InlineData("windows:PCI\\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\\6&1 Intel(R) Ethernet Controller I226-V", "2.1.3.14", "intel-i226v-link-drops")]
    [InlineData("ssd:Samsung SSD 990 PRO with Heatsink 2TB", "0B2QJXD7", "samsung-990-pro-0B2QJXD7")]
    [InlineData("ssd:Samsung SSD 990 PRO 4TB", "0B2QJXD7", null)] // its own line
    [InlineData("chipset:amd AMD Ryzen 5 5600X 6-Core Processor", "3.08.17.735", "amd-chipset-cppc2-windows-11")]
    [InlineData("chipset:amd AMD Ryzen 5 5600X 6-Core Processor", "4.03.03.431", "amd-chipset-installer-not-responding")]
    [InlineData("chipset:intel 13th Gen Intel(R) Core(TM) i7-13700K", "3.08.17.735", null)]
    // Intel's I225-V v1 (REV_01) before driver 1.0.0.30; v3 (REV_03, Intel's B3) never.
    [InlineData("network:PCI\\VEN_8086&DEV_15F3&SUBSYS_86721043&REV_01\\4&1 Intel(R) Ethernet Controller (2) I225-V", "1.0.0.20", "intel-i225v-rev01-2.5g")]
    [InlineData("network:PCI\\VEN_8086&DEV_15F3&SUBSYS_86721043&REV_01\\4&1 Intel(R) Ethernet Controller (2) I225-V", "1.0.0.30", null)]
    [InlineData("network:PCI\\VEN_8086&DEV_15F3&SUBSYS_86721043&REV_03\\4&1 Intel(R) Ethernet Controller (3) I225-V", "1.0.0.20", null)]
    // Windows Update's Intel SST driver: exactly the versions Microsoft names, by the device's name.
    [InlineData("windows:INTELAUDIO\\CTLR_DEV_A0C8&LINKTYPE_06 Intel® Smart Sound Technology (Intel® SST) Audio Controller", "10.29.0.5152", "intel-sst-10.29.0.5152")]
    [InlineData("windows:INTELAUDIO\\CTLR_DEV_A0C8&LINKTYPE_06 Intel® Smart Sound Technology (Intel® SST) Audio Controller", "10.29.0.5714", null)]
    public void EachProviderMatchesItsOwnDevicesOnly(string subject, string version, string? id) =>
        Assert.Equal(id, BuiltIn(subject, version)?.Id);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", "621.01", "update:621.12")]
    [InlineData("NVIDIA GeForce RTX 5070 Ti Laptop GPU", "621.01", "update:621.12")]
    [InlineData("NVIDIA GeForce RTX 5080", "621.12", null)]
    [InlineData("NVIDIA GeForce RTX 4090", "621.01", null)]
    public void NvidiasOwnVideoFreezeIsOnlyOnRtx50(string card, string version, string? advice)
    {
        var found = BuiltIn(DriverAdvisories.Gpu("nvidia", card), version);
        Assert.Equal(advice, found is null ? null : $"{found.Do.ToString().ToLowerInvariant()}:{found.To}");
    }

    [Theory]
    [InlineData(26100, "731120WD", true)]
    [InlineData(26300, "731030WD", true)]
    [InlineData(22631, "731120WD", false)] // 23H2: the host memory buffer fault needs 24H2
    [InlineData(26100, "731130WD", false)] // the fix
    [InlineData(26100, "620311WD", false)] // another line
    public void FirmwareFaultsHoldOnlyFromTheirWindowsBuild(int build, string firmware, bool advised) =>
        Assert.Equal(advised, BuiltIn("ssd:WD_BLACK SN770 2TB", firmware, new([], build)) is not null);

    // ---- Ranges and conditions ----

    private static DriverAdvisories.Advisory Entry(string? from, string? before, string? game = null, int minBuild = 0, int maxBuild = int.MaxValue) =>
        new("t", "^chipset:amd\\b", from, before, "test", DriverAdvisories.Advice.Update, "9.0", "https://example.com", "2026-10-29", game, minBuild, maxBuild);

    [Theory]
    [InlineData("1.9", false)]
    [InlineData("2.0", true)] // From is the first affected
    [InlineData("2.5.1", true)]
    [InlineData("3.0", false)] // Before is the first without the problem
    [InlineData("v2.1", true)]
    [InlineData("unknown", false)]
    public void FromIsInclusiveAndBeforeExclusive(string version, bool advised) =>
        Assert.Equal(advised, DriverAdvisories.Find([Entry("2.0", "3.0")], "chipset:amd", version, Win11) is not null);

    [Theory]
    [InlineData(null, "3.0", "0.1", true)] // every earlier one
    [InlineData("2.0", null, "99.0", true)] // not fixed yet
    [InlineData(null, null, "5.0", true)] // every version
    [InlineData(null, null, "not a version", false)]
    public void AMissingBoundIsOpen(string? from, string? before, string version, bool advised) =>
        Assert.Equal(advised, DriverAdvisories.Find([Entry(from, before)], "chipset:amd", version, Win11) is not null);

    [Fact]
    public void ConditionsNarrowAnEntry()
    {
        var game = Entry(null, null, game: "Blade Zero");
        Assert.Null(DriverAdvisories.Find([game], "chipset:amd", "1.0", Win11));
        Assert.NotNull(DriverAdvisories.Find([game], "chipset:amd", "1.0", WithPbz)); // title contains it, any case
        var build = Entry(null, null, minBuild: 22000, maxBuild: 26099);
        Assert.Null(DriverAdvisories.Find([build], "chipset:amd", "1.0", new([], 19045)));
        Assert.NotNull(DriverAdvisories.Find([build], "chipset:amd", "1.0", new([], 22631)));
        Assert.Null(DriverAdvisories.Find([build], "chipset:amd", "1.0", new([], 26100)));
        Assert.Null(DriverAdvisories.Find([build], "chipset:intel", "1.0", new([], 22631))); // another device
    }

    [Theory]
    [InlineData("0B2QJXD7", "1B2QJXD7", -1)]
    [InlineData("8B2QJXD7", "1B2QJXD7", 1)]
    [InlineData("0B2QJXG7", "1B2QJXD7", null)] // the 4 TB's own line
    [InlineData("731120WD", "731130WD", -1)]
    [InlineData("620311WD", "731130WD", null)]
    [InlineData("ABCDEFWD", "731130WD", null)]
    public void FirmwareComparesOnlyWithinItsLine(string a, string b, int? sign) =>
        Assert.Equal(sign, DriverAdvisories.Firmware(a, b) is { } c ? Math.Sign(c) : null);

    [Fact]
    public void AnAsusBiosComparesByItsNumber()
    {
        Assert.True(DriverAdvisories.Compare("bios", "3636", "3644") < 0);
        Assert.True(DriverAdvisories.Compare("bios", "GA402RJ.319", "318") > 0);
        Assert.Null(DriverAdvisories.Compare("bios", "F12a", "3644"));
    }

    [Fact]
    public void AnEntryThatDoesntReadIsLeftOut()
    {
        var json = """
            { "serial": 7, "advisories": [
              { "id": "ok", "device": "^x:", "why": "w", "do": "skip", "url": "https://a.example", "date": "2026-10" },
              { "id": "bad-pattern", "device": "(", "why": "w", "do": "skip", "url": "https://a.example", "date": "2026-10-29" },
              { "id": "no-source", "device": "^x:", "why": "w", "do": "skip", "date": "2026-10-29" },
              { "id": "plain-http", "device": "^x:", "why": "w", "do": "skip", "url": "http://a.example", "date": "2026-10-29" },
              { "id": "no-date", "device": "^x:", "why": "w", "do": "skip", "url": "https://a.example" },
              { "id": "update-nowhere", "device": "^x:", "why": "w", "do": "update", "url": "https://a.example", "date": "2026-10-29" }
            ] }
            """;
        var table = DriverAdvisories.Parse(Encoding.UTF8.GetBytes(json))!;
        Assert.Equal(7, table.Serial);
        Assert.Equal("ok", Assert.Single(table.Advisories).Id);
        Assert.Null(DriverAdvisories.Parse(Encoding.UTF8.GetBytes("""{ "advisories": [] }""")));
    }

    // ---- The feed ----

    private static (ECDsa Key, byte[] Public) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, P256.PublicKey(key));
    }

    private static string Table(long serial, string id = "feed-entry") => $$"""
        { "serial": {{serial}}, "advisories": [
          { "id": "{{id}}", "device": "^chipset:amd\\b", "from": "9.0", "why": "only the feed knows", "do": "skip", "url": "https://example.com", "date": "2026-10-29" }
        ] }
        """;

    private static string Envelope(ECDsa key, string table, bool highS = false)
    {
        var payload = Encoding.UTF8.GetBytes(table);
        var signature = P256.LowS(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        if (highS) signature = HighS(signature);
        return JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
    }

    /// <summary>The same signature with s turned to n - s: valid ECDSA, but the malleated form Exo refuses.</summary>
    private static byte[] HighS(byte[] signature)
    {
        var n = System.Numerics.BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);
        var s = new System.Numerics.BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        var high = (n - s).ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. signature.AsSpan(0, 32), .. new byte[32 - high.Length], .. high];
    }

    [Fact]
    public void OnlyAFeedSignedWithExosKeyOpens()
    {
        var (key, pub) = NewKey();
        var (other, _) = NewKey();
        var good = Envelope(key, Table(2099010101));
        Assert.Equal(2099010101, DriverAdvisories.Open(good, pub)?.Serial);
        Assert.Null(DriverAdvisories.Open(Envelope(other, Table(2099010101)), pub)); // another key
        Assert.Null(DriverAdvisories.Open(Envelope(key, Table(2099010101), highS: true), pub)); // malleated
        using var doc = JsonDocument.Parse(good);
        var tampered = Encoding.UTF8.GetBytes(Table(2099010101, "changed"));
        Assert.Null(DriverAdvisories.Open(JsonSerializer.Serialize(new { payload = Convert.ToBase64String(tampered), signature = doc.RootElement.GetProperty("signature").GetString() }), pub));
        Assert.Null(DriverAdvisories.Open("not json", pub));
        Assert.Null(DriverAdvisories.Open("""{ "payload": "!!", "signature": "AA==" }""", pub));
        Assert.Null(DriverAdvisories.Open(good, Base64Url.DecodeFromChars(DriverAdvisories.PublicKey))); // a test key isn't Exo's
    }

    [Fact]
    public void AFeedSignedByTheNodeSignerOpensInExo()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "DriverAdvisories", "node-signed.json")));
        var key = Base64Url.DecodeFromChars(string.Concat(doc.RootElement.GetProperty("key").EnumerateArray().Select(part => part.GetString())));
        var envelopes = doc.RootElement.GetProperty("envelopes").EnumerateArray().Select(e => e.GetRawText()).ToList();
        Assert.NotEmpty(envelopes);
        Assert.All(envelopes, e => Assert.Equal("test-feed-only", Assert.Single(DriverAdvisories.Open(e, key)!.Advisories).Id));
    }

    [Fact]
    public void AnOlderFeedThanTheOneKeptIsRefused()
    {
        var (key, pub) = NewKey();
        var newer = Envelope(key, Table(2099010102));
        var older = Envelope(key, Table(2099010101));
        Assert.Equal(2099010102, DriverAdvisories.Accept(newer, older, pub).Serial);
        Assert.Equal(2099010102, DriverAdvisories.Accept(newer, newer, pub).Serial); // the same again is fine
        Assert.Throws<FormatException>(() => DriverAdvisories.Accept(older, newer, pub));
        Assert.Throws<FormatException>(() => DriverAdvisories.Accept("{}", null, pub));
    }

    private DriverAdvisories Advisories(byte[] pub, Func<string?> fetch, DriverAdvisories.Table? builtIn = null) =>
        new(builtIn ?? new DriverAdvisories.Table(2026102901, [Entry(null, null)]), pub, () => Win11,
            new LatestCache<string>("test advisories", TimeSpan.FromDays(1), Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".json")),
            _ => Task.FromResult(fetch()));

    [Fact]
    public async Task TheBuiltInTableStandsUntilAVerifiedNewerFeedArrives()
    {
        var (key, pub) = NewKey();
        var (other, _) = NewKey();

        // Not served yet (Exo before the first upload): the built-in table.
        var none = Advisories(pub, () => throw new HttpRequestException("404"));
        await none.RefreshAsync(CancellationToken.None);
        Assert.Equal(2026102901, none.Current.Serial);

        // Signed by another key: refused, the built-in table stands.
        var forged = Advisories(pub, () => Envelope(other, Table(2099010101)));
        await forged.RefreshAsync(CancellationToken.None);
        Assert.Equal(2026102901, forged.Current.Serial);

        // Exo's, but older than what this Exo was built with: the built-in table.
        var stale = Advisories(pub, () => Envelope(key, Table(2026010101)));
        await stale.RefreshAsync(CancellationToken.None);
        Assert.Equal(2026102901, stale.Current.Serial);

        // Exo's and newer: in force, and it holds back what the built-in table didn't know.
        var fresh = Advisories(pub, () => Envelope(key, Table(2099010101)));
        await fresh.RefreshAsync(CancellationToken.None);
        Assert.Equal(2099010101, fresh.Current.Serial);
        Assert.Equal("9.1 skipped: only the feed knows.", fresh.Refuse("chipset:amd", "9.1"));
        Assert.Null(fresh.Refuse("chipset:amd", "8.9"));
    }

    // ---- Re-checked at install ----

    [Fact]
    public async Task WindowsUpdatesBatchLeavesOutAnAdvisedDriverRightBeforeInstalling()
    {
        var item = new DriverItem("windows:740c4b6c-bcfa-4f97-ae49-7bcc6933e696", "windows", "Audio", "Intel", "10.29.0.5000", null, "10.29.0.5152", null, DriverHow.Install, null, false)
        {
            Device = @"INTELAUDIO\CTLR_DEV_A0C8 Intel® Smart Sound Technology (Intel® SST) Audio Controller",
        };
        var drivers = new WindowsUpdateDrivers(_folder, i => DriverAdvisories.Find(DriverAdvisories.BuiltIn.Advisories, i.Subject, i.Latest!, Win11) is { } a ? DriverAdvisories.Skipped(i.Latest!, a) + "." : null);
        var (_, result) = Assert.Single(await drivers.InstallBatchAsync([item], CancellationToken.None));
        Assert.False(result.Ok);
        Assert.Equal("10.29.0.5152 skipped: blue screens.", result.Message);
        Assert.False(Directory.Exists(_folder)); // nothing ran: no result file, no kept driver
    }
}
