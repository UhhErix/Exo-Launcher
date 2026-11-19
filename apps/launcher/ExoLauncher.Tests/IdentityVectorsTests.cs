using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// The shared test vectors (plan §2.10, §2.11, §2.14, §2.15, §2.16): Exo.Identity/Vectors/exo-identity-vectors.json, made by this C# code.
/// Every value is computed here
/// from the fixed inputs written beside it, and every "refused" or "winner" entry is checked against the code as it is
/// written, so the file can't claim what the code doesn't do. ECDSA signatures are random, so a signature already in
/// the file is kept while it still verifies over the same bytes, and all of them are listed with their messages under
/// "signatures".
///
/// Ways the vectors could mislead the other apps:
/// - the file drifts from what Launcher's code does (an edit to either without the other);
/// - a signature in it doesn't verify, verifies over other bytes than the ones listed, or is high-s;
/// - a fork case that a hash-only rule would also pass (the priority cases are built so the loser has the lower hash),
///   or a three- or four-way fork whose head depends on arrival order (each case is taken in every order, the fork
///   alone and with everything that follows it: branches, later changes and re-signs);
/// - a re-signed branch that a checker can't tie back to what lost (each keeps its by, op, device and at), or losers by
///   different authors whose order of re-signing a checker could get from the hashes or the file's order (they're built
///   so only the authors' ranks give it);
/// - an equivocation a checker settles by arrival order or by dropping only one of the two (both are built to beat the
///   change that wins, one by removing its author);
/// - a kit whose header epoch a checker could take for the sync-key epoch or for 0 (it is neither);
/// - a retry path nobody runs (one scalar needs counter 1);
/// - an account entry that can do more than add PCs, or a PC it added (or one that PC added) that can remove or rename a
///   PC that came before it, or add an entry (so a taken-over account could lock the owner out);
/// - two apps on one PC each signing the same lost change again (the second app's take must find the first's re-sign
///   in device-list.json and sign nothing);
/// - the copies differ between repos (the SHA-256 in plan §2.11 is over the file with LF line endings, since git may
///   check it out with CRLF).
/// To write the file again after an intended change: set EXO_WRITE_IDENTITY_VECTORS=1, run this class, then put the new
/// SHA-256 here and in plan §2.11.
/// </summary>
public sealed class IdentityVectorsTests
{
    internal const string Sha256 = "65a8178b1267153d69b8498e291cb0716c85268cb0ee7b8ed0e0ceb6567d8e3e";
    private static readonly string FilePath = Repo.PathOf("Exo.Identity", "Vectors", "exo-identity-vectors.json");

    // SHA-256("exo vectors/retry sync key")[..24] || u64 big-endian 4037960311: the first key of that series whose sync
    // box scalar is out of range at counter 0 (HKDF-Expand gives ffffffff557d08de…, above n), found by a search of
    // about 2^32 keys and checked separately with Python's hmac.
    internal const string RetrySyncKey ="767772b521a5e91f31f036b457dacb5836d644cb13779c3c00000000f0ae6277";

    private static readonly BigInteger Order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void TheCheckedInVectorsAreWhatThisCodeMakes()
    {
        var onDisk = File.Exists(FilePath) ? File.ReadAllText(FilePath).ReplaceLineEndings("\n") : null;
        var fixture = onDisk is null ? null : (JsonObject)JsonNode.Parse(onDisk)!;
        var vectors = Build(fixture);
        var canonical = Visible(vectors.ToJsonString(Pretty)) + "\n";
        if (fixture is not null)
            ApplyFixturePlaceholders(vectors, fixture);
        var built = Visible(vectors.ToJsonString(Pretty)) + "\n";
        if (Environment.GetEnvironmentVariable("EXO_WRITE_IDENTITY_VECTORS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, built, new UTF8Encoding(false));
            onDisk = built;
            TestContext.Current.SendDiagnosticMessage("exo-identity-vectors.json SHA-256 " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(built))));
        }
        Assert.NotNull(onDisk);
        Assert.Equal(built, onDisk);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static void ApplyFixturePlaceholders(JsonNode actual, JsonNode fixture)
    {
        if (actual is JsonObject built && fixture is JsonObject expected)
        {
            foreach (var (name, value) in expected)
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == "<generated>")
                    built[name] = text;
                else if (value is not null && built[name] is { } entry)
                    ApplyFixturePlaceholders(entry, value);
            }
        }
        else if (actual is JsonArray actualItems && fixture is JsonArray fixtureItems)
        {
            for (var i = 0; i < Math.Min(actualItems.Count, fixtureItems.Count); i++)
                if (actualItems[i] is { } actualItem && fixtureItems[i] is { } fixtureItem)
                    ApplyFixturePlaceholders(actualItem, fixtureItem);
        }
    }

    [Fact]
    public void EverySignatureInTheVectorsVerifiesAndOnlyOverItsMessage()
    {
        var vectors = (JsonObject)JsonNode.Parse(File.ReadAllText(FilePath))!;
        var signatures = vectors["signatures"]!.AsArray();
        Assert.NotEmpty(signatures);
        foreach (var entry in signatures)
        {
            var key = Hex(entry!["publicKey"]);
            var message = Hex(entry["message"]);
            var signature = Hex(entry["signature"]);
            Assert.True(P256.IsLowS(signature));
            Assert.True(P256.Verify(key, message, signature));
            message[^1] ^= 1;
            Assert.False(P256.Verify(key, message, signature));
        }
    }

    /// <summary>
    /// The file with format characters (bidi overrides and marks, zero-width characters), combining and unassigned
    /// characters, the Hangul fillers and spaces other than U+0020 written as escapes, which the relaxed encoder leaves
    /// raw, so the device-name cases stay visible in an editor and don't turn its text around. They can only stand inside
    /// strings, and JSON reads both spellings as the same string.
    /// </summary>
    private static string Visible(string json)
    {
        var text = new StringBuilder(json.Length);
        foreach (var c in json)
        {
            if (c != ' ' && (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.SpaceSeparator
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark
                    or System.Globalization.UnicodeCategory.OtherNotAssigned || (int)c is 0x115F or 0x1160 or 0x3164 or 0xFFA0))
                text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else text.Append(c);
        }
        return text.ToString();
    }

    private static byte[] Hex(JsonNode? node) => Convert.FromHexString(node!.GetValue<string>());

    private static string H(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);

    private static byte[] Scalar(string name) => SHA256.HashData(Encoding.ASCII.GetBytes("exo vectors/" + name));

    /// <summary>Stops the build when a vector would claim what the code doesn't do.</summary>
    private static void Check(bool holds, string what)
    {
        if (!holds) throw new InvalidOperationException("The code disagrees with the vector: " + what);
    }

    /// <summary>(r, n - s): the twin every ECDSA signature has.</summary>
    internal static byte[] Twin(byte[] signature)
    {
        var s = (Order - new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true)).ToByteArray(isUnsigned: true, isBigEndian: true);
        var twin = new byte[64];
        signature.AsSpan(0, 32).CopyTo(twin);
        s.CopyTo(twin.AsSpan(64 - s.Length));
        return twin;
    }

    /// <summary>Every order of <paramref name="items"/> (the runner's own, so an app's port of it is a straight copy).</summary>
    internal static IEnumerable<IReadOnlyList<T>> Orders<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }
        for (var i = 0; i < items.Count; i++)
            foreach (var rest in Orders([.. items.Take(i), .. items.Skip(i + 1)]))
                yield return [items[i], .. rest];
    }

    /// <summary>
    /// A test copy's key names (/6): a folder's full path, its trailing separator dropped and lowercased (invariant),
    /// hashed with SHA-256 in UTF-8; the first 12 hex digits after " test-". The real folder has no suffix.
    /// </summary>
    private static JsonArray TestSuffixSection()
    {
        string[] folders = [@"C:\Temp\exo-tests-1", @"C:\Temp\exo-tests-1\", @"C:\TEMP\EXO-TESTS-1", @"D:\Exo Tests\Ölf Ünïcode\identity"];
        return new JsonArray([.. folders.Select(folder =>
        {
            var normalized = IdentityStore.Normalize(folder);
            var suffix = CngDeviceKeys.TestSuffix(folder);
            Check(suffix == " test-" + H(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12], "test suffix of " + folder);
            return (JsonNode)new JsonObject { ["folder"] = folder, ["normalized"] = normalized, ["suffix"] = suffix };
        })]);
    }

    /// <summary>
    /// §2.7's merge (/5): each case's a and b, merged in both orders, end the same, the items ordered by type then id.
    /// </summary>
    private static JsonArray SyncMergeSection(string writerId)
    {
        var higher = new string('f', 32);
        SyncBatch.Item Set(string id, long at, string device, int value) => new("setting", id, at, device, JsonValue.Create(value));
        SyncBatch.Item Gone(string id, long at, string device) => new("setting", id, at, device, null);
        (string Why, SyncBatch.Item[] A, SyncBatch.Item[] B)[] cases =
        [
            ("the later modified wins", [Set("volume", 1_761_000_000_000, writerId, 1)], [Set("volume", 1_761_000_001_000, writerId, 2)]),
            ("a tie in modified goes to the higher device", [Set("volume", 1_761_000_000_000, writerId, 1)], [Set("volume", 1_761_000_000_000, higher, 2)]),
            ("a newer delete beats an older body", [Set("theme", 1_761_000_000_000, higher, 1)], [Gone("theme", 1_761_000_001_000, writerId)]),
            ("a newer body beats an older delete", [Gone("theme", 1_761_000_000_000, higher)], [Set("theme", 1_761_000_001_000, writerId, 3)]),
            ("a snapshot and a later batch end the same in either order",
                [Set("a", 1_761_000_000_000, writerId, 1), Set("b", 1_761_000_000_000, writerId, 1), Gone("c", 1_761_000_000_000, writerId)],
                [Set("b", 1_761_000_003_000, higher, 2), Set("c", 1_761_000_002_000, higher, 5), Set("d", 1_761_000_002_500, writerId, 4)]),
        ];
        static JsonArray Json(IEnumerable<SyncBatch.Item> items) => new([.. items.Select(item => (JsonNode)SyncBatch.Json(item))]);
        return new JsonArray([.. cases.Select(c =>
        {
            var ab = SyncBatch.Merge(c.A, c.B);
            var ba = SyncBatch.Merge(c.B, c.A);
            Check(Json(ab).ToJsonString() == Json(ba).ToJsonString(), "merged in either order: " + c.Why);
            return (JsonNode)new JsonObject { ["why"] = c.Why, ["a"] = Json(c.A), ["b"] = Json(c.B), ["merged"] = Json(ab) };
        })]);
    }

    /// <summary>
    /// The shared folder's files (§2.18, /5): the sync key and the recovery box key sealed to this PC's encryption key
    /// for this identity, and device-list.json from the vectors' own device list.
    /// </summary>
    private static JsonObject IdentityFilesSection(DeviceList list, byte[] signerPublicKey, byte[] syncKey)
    {
        using var pc = new EcdhDecrypter(P256.Agreement(Scalar("this pc encryption")));
        using var syncEphemeral = P256.Agreement(Scalar("at rest ephemeral"));
        using var recoveryEphemeral = P256.Agreement(Scalar("at rest ephemeral 2"));
        var exoId = list.ExoId;
        var recoveryKey = Scalar("recovery box key");
        var sealedSync = IdentityFiles.SealSyncKey(pc.PublicKey, exoId, 7, syncKey, syncEphemeral);
        var sealedRecovery = IdentityFiles.SealRecoveryKey(pc.PublicKey, exoId, recoveryKey, recoveryEphemeral);
        Check(IdentityFiles.OpenSyncKey(pc, exoId, sealedSync) is { Epoch: 7 } opened && opened.Key.AsSpan().SequenceEqual(syncKey), "sync-key.sealed opens");
        Check(IdentityFiles.OpenSyncKey(pc, Device.ExoIdOf(signerPublicKey) + "x", sealedSync) is null, "sync-key.sealed opens for no other identity");
        Check(IdentityFiles.OpenRecoveryKey(pc, exoId, sealedRecovery) is { } box && box.AsSpan().SequenceEqual(recoveryKey), "recovery.sealed opens");
        var listFile = IdentityFiles.DeviceListFile(list);
        Check(IdentityFiles.ReadDeviceList(listFile) is { } read && read.Head == list.Head, "device-list.json reads back to the same head");
        using var accountEphemeral = P256.Agreement(Scalar("at rest ephemeral 3"));
        var deviceId = Device.IdOf(signerPublicKey);
        var session = new AccountClient.Session("vector-session-token", "012345" + "6789ab" + "cdef01" + "234567" + "89abcd" + "ef", "google", "someone@example.com");
        var sealedAccount = IdentityFiles.SealAccount(pc.PublicKey, deviceId, session, accountEphemeral);
        Check(IdentityFiles.OpenAccount(pc, deviceId, sealedAccount) == session, "account.sealed opens");
        Check(IdentityFiles.OpenAccount(pc, new string('0', 32), sealedAccount) is null, "account.sealed opens for no other PC");
        using var accountBoxEphemeral = P256.Agreement(Scalar("at rest ephemeral 4"));
        var accountBoxKey = Scalar("account box key");
        var sealedAccountBox = IdentityFiles.SealAccountBoxKey(pc.PublicKey, exoId, accountBoxKey, accountBoxEphemeral);
        Check(IdentityFiles.OpenAccountBoxKey(pc, exoId, sealedAccountBox) is { } accountBox && accountBox.AsSpan().SequenceEqual(accountBoxKey), "account-box.sealed opens");
        Check(IdentityFiles.OpenRecoveryKey(pc, exoId, sealedAccountBox) is null && IdentityFiles.OpenAccountBoxKey(pc, exoId, sealedRecovery) is null,
            "account-box.sealed and recovery.sealed don't read as each other");
        return new JsonObject
        {
            ["folder"] = @"%LOCALAPPDATA%\Exo\identity",
            ["files"] = new JsonArray(IdentityFiles.State, IdentityFiles.SyncKey, IdentityFiles.DeviceList, IdentityFiles.Recovery, IdentityFiles.Account, IdentityFiles.AccountBox),
            ["stateMutex"] = IdentityFiles.StateMutex,
            ["exoId"] = exoId,
            ["encryptionScalar"] = H(Scalar("this pc encryption")),
            ["encryptionPublicKey"] = H(pc.PublicKey),
            ["syncKey"] = new JsonObject
            {
                ["hpkeInfo"] = "exo identity v1/sync key at rest",
                ["aad"] = "the Exo ID in UTF-8",
                ["ephemeralScalar"] = H(Scalar("at rest ephemeral")),
                ["epoch"] = 7,
                ["key"] = H(syncKey),
                ["plaintext"] = "{\"epoch\":7,\"key\":\"" + H(syncKey) + "\"}",
                ["file"] = H(sealedSync),
            },
            ["recovery"] = new JsonObject
            {
                ["hpkeInfo"] = "exo identity v1/recovery box at rest",
                ["aad"] = "the Exo ID in UTF-8",
                ["ephemeralScalar"] = H(Scalar("at rest ephemeral 2")),
                ["key"] = H(recoveryKey),
                ["file"] = H(sealedRecovery),
            },
            ["deviceListFile"] = Text(listFile),
            ["account"] = new JsonObject
            {
                ["hpkeInfo"] = "exo identity v1/account at rest",
                ["aad"] = "this PC's device id in UTF-8",
                ["deviceId"] = deviceId,
                ["ephemeralScalar"] = H(Scalar("at rest ephemeral 3")),
                ["plaintext"] = Text(CanonicalJson.Serialize(AccountClient.SessionJson(session))),
                ["file"] = H(sealedAccount),
            },
            ["accountBox"] = new JsonObject
            {
                ["hpkeInfo"] = "exo identity v1/account box at rest",
                ["aad"] = "the Exo ID in UTF-8",
                ["ephemeralScalar"] = H(Scalar("at rest ephemeral 4")),
                ["key"] = H(accountBoxKey),
                ["file"] = H(sealedAccountBox),
            },
        };
    }

    internal static JsonObject Build(JsonObject? previous)
    {
        var signed = new Signatures(previous?["signatures"] as JsonArray);
        var vectors = new JsonObject
        {
            ["version"] = "exo-identity-vectors/19",
            ["about"] = "Exo identity spec v0 (plan §2; §2.14 lists what changed in /2, §2.15 in /3, §2.16 in /4, §2.18 in /5, §2.19 in /6, §2.7's sync wire and §2.17's profile items in /7, §2.7's rules 6 to 8 in /8: the new sync key after a removal, the link messages and the device list carried in profile items, §2.18's one take under the mutex in /9: two apps on one PC taking one fork sign it again once, §2.17's account.sealed in /10, its profile banner in /11, the account entry and restricted PCs in /12, restricted PCs reaching only restricted PCs and account-box.sealed in /13, the account entry's add after the owner left and the profile's look in /14, the profile's canvas, a media's view, accent2, font and the longer lists in /15, a setup block's gear and build, the hero as blocks, a font on any block and the 96 grid in /16, the profile's stats, watching and the watching showcase in /17, the look and canvas as one item per field, who sees a block, hidden games and what a PC carries across a sign-out in /18, the profile's media (its parts' names and seals, a media request's signed bytes, its box rows) and no JSON nulls (a field with none is left out) in /19). Bytes are lowercase hex unless a field says otherwise; JSON records are as they travel. Every signature is low-s. HPKE itself is checked against RFC 9180 A.3.1.",
        };

        vectors["base32"] = new JsonArray([.. new[] { "", "66", "666f6f626172", "00112233445566778899aabbccddeeff", H(SHA256.HashData("exo"u8)) }
            .Select(hex => (JsonNode)new JsonObject { ["hex"] = hex, ["text"] = Base32.Encode(Convert.FromHexString(hex)) })]);
        vectors["base32Read"] = new JsonArray([.. new[] { "aaise-m2ekv thpce-zvk54-zxpo74", "MZXW6YTB0I", "A81Q", "mzxw6ytboi" }
            .Select(text => (JsonNode)new JsonObject { ["text"] = text, ["hex"] = H(Base32.Decode(text)!) })]);
        vectors["base32Refused"] = new JsonArray([.. new[]
        {
            ("MZ", "leftover bits not zero"),
            ("AAISEM2EKVTHPCEZVK54ZXPO75", "leftover bits not zero"),
            ("M", "no whole number of bytes has this length"),
            ("MY======", "padding"),
            ("MZXW6YTBO9", "9 is not base32"),
        }.Select(r =>
        {
            Check(Base32.Decode(r.Item1) is null, "base32 refuses " + r.Item1);
            return (JsonNode)new JsonObject { ["text"] = r.Item1, ["why"] = r.Item2 };
        })]);

        string[] valid =
        [
            "{\"b\":1,\"a\":[true,false,\"x\"]}",
            "{ \"z\" : { \"y\" : 2 , \"x\" : -3 } }",
            "{\"s\":\"\\u00e9 \\u20ac \\ud83d\\ude00 / \\u0041 \\u007f\"}",
            "{\"s\":\"q\\\" b\\\\ \\b\\f\\n\\r\\t \\u0001 \\u001f\"}",
            "{\"n\":9007199254740991,\"m\":-9007199254740991,\"o\":0}",
            "{\"A\":1,\"_\":2,\"a\":3,\"B\":4,\"\":5}",
            "{\"empty\":{},\"list\":[],\"nested\":[[{\"b\":\"\",\"a\":0}]]}",
        ];
        vectors["canonicalJson"] = new JsonObject
        {
            ["valid"] = new JsonArray([.. valid.Select(input => (JsonNode)new JsonObject
            {
                ["input"] = input,
                ["canonical"] = Text(CanonicalJson.Serialize(CanonicalJson.ParseObject(Encoding.UTF8.GetBytes(input))!)),
            })]),
            ["refused"] = new JsonArray([.. new[]
            {
                "{\"a\":null}", "{\"a\":1.5}", "{\"a\":1.0}", "{\"a\":1e2}", "{\"a\":-0}", "{\"a\":9007199254740992}",
                "{\"é\":1}", "{\"a\":\"\\ud800\"}", "{\"a\":\"\\udc00x\"}", "{\"a\":1,\"a\":2}",
            }.Select(input => (JsonNode)input)]),
        };

        var emoji64 = string.Concat(Enumerable.Repeat(Cp(0x1F600), 64));
        string[] names =
        [
            "Desk PC", Cp(0xC9) + "ric's laptop", Cp(0x1F600), emoji64, new string('x', 64),
            Cp(0x1F9D1, 0x200D, 0x1F4BB) + " Desk", Cp(0x2764, 0xFE0F) + " PC", Cp(0x1F3F3, 0xFE0F, 0x200D, 0x1F308), Cp(0x23, 0xFE0F, 0x20E3) + " Den",
            Cp(0x1F3F4, 0xE0067, 0xE0062, 0xE0073, 0xE0063, 0xE0074, 0xE007F) + " PC", Cp(0x1F642, 0x200D, 0x2194, 0xFE0F),
        ];
        var badNames = new List<(string Name, string Why)>
        {
            ("", "empty"),
            (new string('x', 65), "65 scalar values"),
            (emoji64 + Cp(0x1F600), "65 scalar values"),
            (" Desk PC", "white space first"),
            ("Desk PC ", "white space last"),
            (Cp(0x3000) + "Desk PC", "white space first (U+3000)"),
            ("Desk" + Cp(0x0A) + "PC", "a control"),
            ("Desk" + Cp(0x85) + "PC", "a control (U+0085)"),
            ("Desk" + Cp(0x2028) + "PC", "a line separator"),
            ("Desk" + Cp(0x202E) + "PC", "a bidi override"),
            ("Desk" + Cp(0x2066) + "PC", "a bidi isolate"),
            ("a" + Cp(0x200F) + "b", "U+200F (right-to-left mark), taken in /2"),
            (Cp(0x1F600, 0x200D), "a joiner with no pictograph after it"),
            (Cp(0x200D, 0x1F600), "a joiner with nothing before it"),
            (Cp(0xFE0F) + "PC", "a variation selector with nothing before it"),
            (Cp(0x1F3F4, 0xE0067, 0xE0062) + " PC", "a flag's tags not ended by U+E007F"),
            (Cp(0x1F3F4, 0xE007F), "U+E007F after no tag"),
        };
        int[] invisible =
        [
            0xAD, 0x34F, 0x61C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180C, 0x180D, 0x180E, 0x180F, 0x200B, 0x200C, 0x200D, 0x200E, 0x200F,
            0x2060, 0x2061, 0x2062, 0x2063, 0x2064, 0x2065, 0x206A, 0x206B, 0x206C, 0x206D, 0x206E, 0x206F, 0x3164, 0xFE00, 0xFE0D, 0xFE0E, 0xFE0F,
            0xFEFF, 0xFFA0, 0xFFF0, 0xFFF8, 0x1BCA0, 0x1BCA3, 0x1D173, 0x1D17A, 0xE0000, 0xE0001, 0xE001F, 0xE0020, 0xE007E, 0xE007F, 0xE0080, 0xE0100,
            0xE01EF, 0xE0FFF,
        ];
        badNames.AddRange(invisible.Select(c => ("Desk" + Cp(c) + "PC", "U+" + c.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + " between letters")));
        foreach (var name in names) Check(Device.NameOk(name), "the name " + name + " is taken");
        foreach (var (name, why) in badNames) Check(!Device.NameOk(name), "a name with " + why + " is refused");

        // The lists in the file are the code's, checked code point by code point.
        string[] invisibleRanges =
        [
            "0000-001F", "007F-009F", "00AD", "034F", "061C", "115F-1160", "17B4-17B5", "180B-180F", "200B-200F", "2028-2029", "202A-202E", "2060-206F",
            "3164", "FE00-FE0F", "FEFF", "FFA0", "FFF0-FFF8", "1BCA0-1BCA3", "1D173-1D17A", "E0000-E0FFF",
        ];
        string[] pictographs = ["203C", "2049", "2100-2BFF", "3030", "303D", "3297", "3299", "1F000-1FAFF"];
        string[] keycapBases = ["0023", "002A", "0030-0039", "00A9", "00AE"];
        string[] spaces = ["0020", "00A0", "1680", "2000-200A", "202F", "205F", "3000"];
        var (invisibleSet, pictographSet, keycapSet, spaceSet) = (Ranges(invisibleRanges), Ranges(pictographs), Ranges(keycapBases), Ranges(spaces));
        for (var c = 0; c <= 0x10FFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF) continue;
            Check(Device.Invisible(c) == In(invisibleSet, c), "refused anywhere: as the file lists");
            Check(Device.NameOk(Cp(0x1F600, 0x200D, c)) == In(pictographSet, c), "a joiner joins the file's pictographs");
            Check(Device.NameOk(Cp(c, 0xFE0F)) == (In(pictographSet, c) || In(keycapSet, c)), "a selector follows the file's pictographs and keycap bases");
            Check(Device.NameOk(Cp(c) + "x") == !(In(spaceSet, c) || In(invisibleSet, c)), "white space first: as the file lists");
        }
        vectors["deviceNames"] = new JsonObject
        {
            ["counts"] = "Unicode scalar values, 1 to 64",
            ["refusedAnywhere"] = new JsonArray([.. invisibleRanges.Select(r => (JsonNode)r)]),
            ["exceptInEmoji"] = new JsonObject
            {
                ["joiner"] = "U+200D only between two pictographs; the one before may also be U+FE0F",
                ["selectors"] = "U+FE0E and U+FE0F only right after a pictograph or a keycap base",
                ["tags"] = "U+E0020 to U+E007E only right after U+1F3F4 or another of them, and right before another of them or U+E007F",
                ["cancelTag"] = "U+E007F only right after one of U+E0020 to U+E007E",
                ["pictographs"] = new JsonArray([.. pictographs.Select(r => (JsonNode)r)]),
                ["keycapBases"] = new JsonArray([.. keycapBases.Select(r => (JsonNode)r)]),
            },
            ["refusedFirstOrLast"] = new JsonArray([.. spaces.Select(r => (JsonNode)r)]),
            ["accepted"] = new JsonArray([.. names.Select(name => (JsonNode)name)]),
            ["refused"] = new JsonArray([.. badNames.Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["why"] = b.Why })]),
        };

        var prk = Scalar("prk");
        vectors["deriveScalar"] = new JsonArray(
            Derived(prk, "box", null), Derived(prk, "sign", null), Derived(prk, "exo sync box v1", null),
            Derived(Convert.FromHexString(RetrySyncKey), "exo sync box v1", "a sync key whose sync box needs a second candidate"));

        var syncKey = Scalar("sync key");
        var history = Envelope.SyncSubKey(syncKey, "browser-history");
        vectors["sync"] = new JsonObject
        {
            ["syncKey"] = H(syncKey),
            ["subKeys"] = new JsonArray([.. new[] { "launcher", "cinema", "browser", "browser-history" }
                .Select(app => (JsonNode)new JsonObject { ["app"] = app, ["key"] = H(Envelope.SyncSubKey(syncKey, app)) })]),
            ["boxes"] = new JsonArray(Box("sync key", syncKey), Box("browser-history sub-key", history)),
        };

        using var box = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(syncKey)));
        var boxId = Mailbox.BoxId(box.PublicKey);
        var challenge = Scalar("challenge");
        var auth = Mailbox.AuthMessage(boxId, challenge);
        var authSignature = signed.Sign(box, auth);
        vectors["mailboxAuth"] = new JsonObject
        {
            ["boxPublicKey"] = H(box.PublicKey),
            ["boxId"] = boxId,
            ["challenge"] = H(challenge),
            ["message"] = H(auth),
        };

        var twin = Twin(authSignature);
        Check(!P256.IsLowS(twin) && !P256.Verify(box.PublicKey, auth, twin) && P256.LowS(twin).SequenceEqual(authSignature), "a high-s twin is refused and lowers back");
        vectors["highS"] = new JsonObject
        {
            ["halfOrder"] = H((Order >> 1).ToByteArray(isUnsigned: true, isBigEndian: true)),
            ["publicKey"] = H(box.PublicKey),
            ["message"] = H(auth),
            ["lowS"] = H(authSignature),
            ["highS"] = H(twin),
        };

        vectors["padding"] = new JsonArray([.. new[] { 0, 1, 252, 253, 508, 509, 1020, 1021, 32764, 32765, Envelope.MaxData }
            .Select(n => (JsonNode)new JsonObject { ["dataBytes"] = n, ["paddedBytes"] = Envelope.Pad(new byte[n]).Length })]);
        vectors["paddingExample"] = new JsonObject { ["data"] = H("abc"u8), ["padded"] = H(Envelope.Pad("abc"u8)) };
        var overPadded = new byte[512];
        overPadded[3] = 10;
        "0123456789"u8.CopyTo(overPadded.AsSpan(4));
        var junk = Envelope.Pad("abc"u8);
        junk[200] = 1;
        var pastEnd = new byte[256];
        pastEnd[3] = 253;
        vectors["paddingRefused"] = new JsonArray([.. new[]
        {
            (overPadded, "10 bytes in 512: over-padded, 256 holds them"),
            (junk, "not zeros after the data"),
            (pastEnd, "a length past the end"),
            (new byte[300], "not a padding size"),
        }.Select(r =>
        {
            Check(Envelope.Unpad(r.Item1) is null, "padding refused: " + r.Item2);
            return (JsonNode)new JsonObject { ["padded"] = H(r.Item1), ["why"] = r.Item2 };
        })]);

        using var writer = new VectorSigner(P256.Signer(Scalar("writer")), signed);
        using var recipient = new EcdhDecrypter(P256.Agreement(Scalar("recipient")));
        using var ephemeral = P256.Agreement(Scalar("ephemeral"));
        var writerId = Device.IdOf(writer.PublicKey);

        var batch = SyncBatch.Write("launcher",
        [
            new("wishlist", "steam:570", 1_761_000_000_000, writerId, new JsonObject { ["store"] = "steam", ["appId"] = 570 }),
            new("wishlist", "steam:730", 1_761_000_000_500, writerId, null),
        ]);
        var batchText = Text(batch);
        string Items(string items) => "{\"app\":\"launcher\",\"items\":[" + items + "],\"v\":1}";
        var item = "{\"body\":1,\"device\":\"" + writerId + "\",\"id\":\"steam:570\",\"modified\":1761000000000,\"type\":\"wishlist\"}";
        (string Text, string App, string Why)[] badBatches =
        [
            ("{\"items\":[{\"app\":\"launcher\",\"id\":\"steam:570\",\"modified\":1761000000,\"type\":\"wishlist\"}]}", "launcher", "no v or app (the old vector's shape)"),
            (batchText.Replace("\"app\":", "\"app\": "), "launcher", "not in canonical form"),
            (batchText, "cinema", "another app's batch"),
            (Items(""), "launcher", "no items"),
            (Items(item + "," + item), "launcher", "an item named twice"),
            (Items(item.Replace("{\"body\":1,", "{\"body\":1,\"deleted\":true,")), "launcher", "both body and deleted"),
            (Items(item.Replace("{\"body\":1,", "{\"deleted\":false,")), "launcher", "deleted is false"),
            (Items(item.Replace(writerId, writerId.ToUpperInvariant())), "launcher", "a device id in capitals"),
            (Items(item.Replace("wishlist", "Wishlist")), "launcher", "a type outside a-z, 0-9, '-'"),
            (Items(item.Replace("steam:570", "steam 570")), "launcher", "a space in an id"),
            (batchText[..^"\"v\":1}".Length] + "\"more\":1,\"v\":1}", "launcher", "an unknown field in the batch"),
            (Items(item.Replace("{\"body\":1,", "{\"body\":1,\"color\":\"red\",")), "launcher", "an unknown field in an item"),
            (Items(item.Replace("{\"body\":1,", "{\"color\":\"red\",")), "launcher", "an unknown field in place of body"),
            (batchText[..^"1}".Length] + "2}", "launcher", "a v this reader doesn't know (a new field comes with a new v)"),
        ];
        Check(SyncBatch.Read(batch, "launcher") is { Count: 2 }, "the batch reads back");
        foreach (var (text, app, why) in badBatches) Check(SyncBatch.Read(Encoding.UTF8.GetBytes(text), app) is null, "batch refused: " + why);
        vectors["syncMerge"] = SyncMergeSection(writerId);
        vectors["syncBatch"] = new JsonObject
        {
            ["app"] = "launcher",
            ["canonical"] = batchText,
            ["refused"] = new JsonArray([.. badBatches.Select(b => (JsonNode)new JsonObject { ["text"] = b.Text, ["app"] = b.App, ["why"] = b.Why })]),
        };

        using var eN = P256.Agreement(Scalar("link new pc"));
        using var eE = P256.Agreement(Scalar("link existing pc"));
        var nPub = P256.PublicKey(eN);
        var ePub = P256.PublicKey(eE);
        var shared = P256.Agree(eN, ePub)!;
        var linkKeys = LinkHandshake.Derive(shared, "04821093", nPub, ePub);

        var mailbox = Scalar("mailbox")[..16];
        var launcherKey = Envelope.SyncSubKey(syncKey, "launcher");
        var syncNonce = Scalar("sync nonce")[..12];
        var linkNonce = Scalar("link nonce")[..12];
        var linkData = "{\"t\":\"offer\"}"u8.ToArray();
        var linkEnvelope = Envelope.SealAes(Envelope.LinkKind, 0, new byte[16], linkKeys.Key, linkData, linkNonce);
        Check(Envelope.OpenLink(linkEnvelope, linkKeys.Key) is { } opened && opened.SequenceEqual(linkData), "the link envelope opens under link.key");
        vectors["envelopes"] = new JsonArray(
            new JsonObject
            {
                ["kind"] = 1,
                ["mailboxId"] = H(mailbox),
                ["epoch"] = 7,
                ["app"] = "launcher",
                ["key"] = H(launcherKey),
                ["nonce"] = H(syncNonce),
                ["aad"] = H([.. "exo-env/1"u8, .. mailbox, 0x02, 0x01, 0x00, 0x00, 0x00, 0x07]),
                ["data"] = H(batch),
                ["dataIs"] = "syncBatch.canonical",
                ["signerPublicKey"] = H(writer.PublicKey),
                ["envelope"] = H(Envelope.Sign(Envelope.SealAes(Envelope.SyncKind, 7, mailbox, launcherKey, batch, syncNonce), writer)),
            },
            new JsonObject
            {
                ["kind"] = 2,
                ["mailboxId"] = H(mailbox),
                ["recipientScalar"] = H(Scalar("recipient")),
                ["recipientPublicKey"] = H(recipient.PublicKey),
                ["ephemeralScalar"] = H(Scalar("ephemeral")),
                ["hpkeInfo"] = "exo-env/1 to-device",
                ["aad"] = H([.. "exo-env/1"u8, .. mailbox, 0x02, 0x02, 0x00, 0x00, 0x00, 0x00]),
                ["data"] = H(syncKey),
                ["signerPublicKey"] = H(writer.PublicKey),
                ["envelope"] = H(Envelope.SealToDevice(mailbox, recipient.PublicKey, syncKey, writer, ephemeral)!),
            },
            new JsonObject
            {
                ["kind"] = 4,
                ["mailboxId"] = H(new byte[16]),
                ["key"] = H(linkKeys.Key),
                ["keyIs"] = "link.key",
                ["nonce"] = H(linkNonce),
                ["aad"] = H([.. "exo-env/1"u8, .. new byte[16], 0x02, 0x04, 0x00, 0x00, 0x00, 0x00]),
                ["data"] = H(linkData),
                ["envelope"] = H(linkEnvelope),
            });

        vectors["link"] = new JsonObject
        {
            ["nameplate"] = "04821093",
            ["newPcScalar"] = H(Scalar("link new pc")),
            ["newPcPublicKey"] = H(nPub),
            ["existingPcScalar"] = H(Scalar("link existing pc")),
            ["existingPcPublicKey"] = H(ePub),
            ["commitment"] = H(LinkHandshake.Commitment(ePub)),
            ["qr"] = LinkHandshake.QrText("04821093", nPub),
            ["shared"] = H(shared),
            ["key"] = H(linkKeys.Key),
            ["matchCode"] = linkKeys.MatchCode,
        };

        var code = Scalar("recovery code")[..16];
        var recovery = RecoveryCode.Read(RecoveryCode.Format(code))!;
        using var recoveryBox = recovery.BoxKey();
        using var entry = new VectorSigner(P256.Signer(recovery.SignScalar), signed);
        using var kitEphemeral = P256.Agreement(Scalar("kit ephemeral"));
        var exoId = Device.ExoIdOf(writer.PublicKey);
        var kit = new RecoveryCode.Kit(exoId, 3, syncKey);
        // This code is the identity's first (deviceList below adds it first), so its number, the kit's header epoch, is
        // 1: neither 0 nor the sync-key epoch (3) the kit carries.
        const uint codeNumber = 1;
        var sealedKit = RecoveryCode.SealKit(kit, codeNumber, recovery.SealPublicKey, recovery.BoxId, writer, kitEphemeral)!;
        Check(recovery.OpenKit(sealedKit) is { } openedKit && openedKit.By == writerId && openedKit.Kit.SyncKey.SequenceEqual(syncKey) &&
              openedKit.Kit.Epoch == 3 && openedKit.Kit.ExoId == exoId && openedKit.CodeNumber == codeNumber && Envelope.SignedBy(sealedKit, writer.PublicKey),
              "the kit opens under the code alone and names its PC and its code's number");
        var recoveryBoxBytes = Convert.FromHexString(recovery.BoxId);
        var kitBytes = RecoveryCode.KitJson(kit, writerId);
        var noNumber = Envelope.SealToDevice(recoveryBoxBytes, recovery.SealPublicKey, kitBytes, writer, kitEphemeral, 0)!;
        var syncEpoch = Envelope.SealToDevice(recoveryBoxBytes, recovery.SealPublicKey, kitBytes, writer, kitEphemeral, 3)!;
        Check(recovery.OpenKit(noNumber) is null, "a kit with header epoch 0 is refused");
        Check(recovery.OpenKit(syncEpoch) is { CodeNumber: 3 } wrong && wrong.CodeNumber != codeNumber, "a kit with the sync-key epoch in its header names another code");
        vectors["recovery"] = new JsonObject
        {
            ["code"] = H(code),
            ["text"] = RecoveryCode.Format(code),
            ["boxScalar"] = H(recovery.BoxScalar),
            ["boxPublicKey"] = H(recoveryBox.PublicKey),
            ["boxId"] = recovery.BoxId,
            ["signScalar"] = H(recovery.SignScalar),
            ["signPublicKey"] = H(entry.PublicKey),
            ["sealScalar"] = H(recovery.SealScalar),
            ["sealPublicKey"] = H(recovery.SealPublicKey),
            ["kit"] = new JsonObject { ["exoId"] = exoId, ["epoch"] = 3, ["syncKey"] = H(syncKey), ["by"] = writerId },
            ["kitIs"] = "a kind 0x02 envelope to sealPublicKey (the entry's encPub), mailbox id boxId, signed by the PC that posts it (by)",
            ["headerEpoch"] = codeNumber,
            ["headerEpochIs"] = "the recovery box's own key counter: the code's number, 1 for the identity's first code (deviceList adds this one first), one more for each after it; not the sync-key epoch in the kit (3)",
            ["plaintext"] = Text(kitBytes),
            ["ephemeralScalar"] = H(Scalar("kit ephemeral")),
            ["hpkeInfo"] = "exo-env/1 to-device",
            ["aad"] = H([.. "exo-env/1"u8, .. recoveryBoxBytes, 0x02, 0x02, 0x00, 0x00, 0x00, 0x01]),
            ["signerPublicKey"] = H(writer.PublicKey),
            ["sealedKit"] = H(sealedKit),
            ["refusedKits"] = new JsonArray(
                new JsonObject { ["sealedKit"] = H(noNumber), ["why"] = "header epoch 0: no code number (the /3 form)" },
                new JsonObject { ["sealedKit"] = H(syncEpoch), ["why"] = "header epoch 3: the kit's sync-key epoch, not its code's number (1)" }),
        };

        // A second code, which the first replaces itself with.
        var recovery2 = RecoveryCode.Read(RecoveryCode.Format(Scalar("recovery code 2")[..16]))!;
        using var entry2 = new VectorSigner(P256.Signer(recovery2.SignScalar), signed);

        using var laptop = new VectorSigner(P256.Signer(Scalar("laptop")), signed);
        using var laptopEnc = P256.Agreement(Scalar("laptop encryption"));
        using var writerEnc = P256.Agreement(Scalar("writer encryption"));
        var laptopId = Device.IdOf(laptop.PublicKey);
        var deskRecord = Device.Create(writer, P256.PublicKey(writerEnc), "Desk PC", ["launcher", "cinema"], 1_761_000_000);
        var laptopRecord = Device.Create(laptop, P256.PublicKey(laptopEnc), Cp(0xC9) + "ric's laptop", ["launcher"], 1_761_000_100);
        var entryRecord = Device.Create(entry, recovery.SealPublicKey, "Recovery code", [], 1_761_000_200, recovery: true);
        var entry2Record = Device.Create(entry2, recovery2.SealPublicKey, "Recovery code", [], 1_761_000_500, recovery: true);
        var list = DeviceList.Start(writer, deskRecord, 1_761_000_000);
        list.Add(writer, laptopRecord, 1_761_000_100);
        list.Add(writer, entryRecord, 1_761_000_200);
        Check(list.CodeNumber == codeNumber, "the vector's code is the identity's first: number 1");
        list.Rename(laptop, laptopId, "Travel laptop", 1_761_000_300);
        list.Remove(laptop, writerId, 1_761_000_400);
        // The code replaces itself with a new code's entry: one change, signed by the old entry.
        list.Add(entry, entry2Record, 1_761_000_500);
        Check(list.Find(Device.IdOf(entry.PublicKey)) is null && list.Find(Device.IdOf(entry2.PublicKey)) is { Recovery: true }, "the new entry takes the old one's place");
        Check(list.CodeNumber == 2, "the code that replaced it is number 2");
        vectors["deviceList"] = new JsonObject
        {
            ["exoId"] = list.ExoId,
            ["changes"] = new JsonArray([.. list.Changes.Select(change => change.DeepClone())]),
            ["hashed"] = new JsonArray([.. list.Changes.Select(change => (JsonNode)Text(Hashed(change)))]),
            ["hashes"] = new JsonArray([.. list.Changes.Select(change => (JsonNode)CanonicalJson.Hash(change))]),
            ["canonical"] = new JsonArray([.. list.Changes.Select(change => (JsonNode)Text(CanonicalJson.Serialize(change)))]),
            ["devicesAtEnd"] = new JsonArray([.. list.Devices.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => (JsonNode)d.Id)]),
            ["codeNumberAtEnd"] = list.CodeNumber,
        };

        vectors["identityFiles"] = IdentityFilesSection(list, writer.PublicKey, syncKey);
        vectors["testSuffix"] = TestSuffixSection();

        var (forks, refusedChanges, acceptedChanges) = Forks(signed, writer, deskRecord, laptop, laptopRecord, entry, entryRecord, entry2Record);
        vectors["forks"] = forks;
        vectors["deviceListAccepted"] = acceptedChanges;
        vectors["deviceListRefused"] = refusedChanges;

        vectors["syncWire"] = SyncWireSection(signed, writer, syncKey);
        vectors["profile"] = ProfileSection(writer.PublicKey, list.Changes[0]);
        vectors["syncRules"] = SyncRulesSection(signed, linkKeys.Key);
        vectors["takeChanges"] = TakeChangesSection(signed);
        vectors["accountEntry"] = AccountEntrySection(signed);
        vectors["rejoin"] = RejoinSection(signed);
        vectors["look"] = LookSection(Device.IdOf(writer.PublicKey));
        vectors["canvas"] = CanvasSection(Device.IdOf(writer.PublicKey));
        vectors["gear"] = GearSection(Device.IdOf(writer.PublicKey));
        vectors["build"] = BuildSection(Device.IdOf(writer.PublicKey));
        vectors["hero"] = HeroSection(Device.IdOf(writer.PublicKey));
        vectors["stats"] = StatsSection(Device.IdOf(writer.PublicKey));
        vectors["watching"] = WatchingSection(Device.IdOf(writer.PublicKey));
        vectors["fields"] = FieldsSection(Device.IdOf(writer.PublicKey));
        vectors["privacy"] = PrivacySection(Device.IdOf(writer.PublicKey));
        vectors["carry"] = CarrySection(Device.IdOf(writer.PublicKey));
        vectors["media"] = MediaSection(syncKey);

        vectors["signatures"] = signed.Used;
        return vectors;
    }

    /// <summary>
    /// /7, the sync box on the wire (plan §2.6, §2.7): the frames a client sends and reads for the shared box of this
    /// file's sync key (mailboxAuth's login, a put of a launcher batch, its answer, a since and the frames that replay
    /// it, a trim), the cursor rule, the pace every client keeps, and the greedy split at its edge.
    /// </summary>
    private static JsonObject SyncWireSection(Signatures signed, VectorSigner writer, byte[] syncKey)
    {
        using var box = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(syncKey)));
        var boxId = Mailbox.BoxId(box.PublicKey);
        var boxIdBytes = Convert.FromHexString(boxId);
        var challenge = Scalar("challenge");
        var authMessage = Mailbox.AuthMessage(boxId, challenge);
        byte[] auth = [Mailbox.AuthFrame, .. box.PublicKey, .. signed.Sign(box, authMessage)];
        Check(auth.Length == 130 && P256.Verify(box.PublicKey, authMessage, auth[66..]), "the login frame is 0x20, the box key and its signature over mailboxAuth.message");
        var writerId = Device.IdOf(writer.PublicKey);
        var batch = SyncBatch.Write("launcher", [new("setting", "appearance", 1_761_000_000_000, writerId, JsonValue.Create("void"))]);
        var subKey = Envelope.SyncSubKey(syncKey, "launcher");
        var envelope = Envelope.Sign(Envelope.SealAes(Envelope.SyncKind, 1, boxIdBytes, subKey, batch, Scalar("frames nonce")[..12]), writer);
        Check(Envelope.OpenSync(envelope, boxIdBytes, subKey, [writer.PublicKey]) is { } opened && opened.Data.SequenceEqual(batch), "the put's envelope opens under the launcher sub-key");
        static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        const uint Seq = 11, At = 1_761_000_000;
        var frames = new JsonObject
        {
            ["challenge"] = H([Mailbox.ChallengeFrame, .. challenge]),
            ["auth"] = H(auth),
            ["ready"] = H([Mailbox.ReadyFrame, .. U32(6), .. U32(10)]),
            ["put"] = H([Mailbox.PutFrame, .. envelope]),
            ["stored"] = H([Mailbox.StoredFrame, .. U32(Seq)]),
            ["since"] = H([Mailbox.SinceFrame, .. U32(10)]),
            ["message"] = H([Mailbox.MessageFrame, .. U32(Seq), .. U32(At), .. envelope]),
            ["caughtUp"] = H([Mailbox.CaughtUpFrame, .. U32(Seq), 0]),
            ["trim"] = H([Mailbox.TrimFrame, .. U32(10)]),
        };

        var cursors = new[] { (0u, 1u, 0u), (0u, 1u, 5u), (4u, 5u, 9u), (3u, 5u, 9u), (10u, 1u, 9u), (9u, 1u, 9u), (0u, 2u, 9u) };
        var cursorMissed = new JsonArray([.. cursors.Select(c => (JsonNode)new JsonObject
        {
            ["cursor"] = c.Item1, ["first"] = c.Item2, ["last"] = c.Item3, ["missed"] = SyncSession.CursorMissed(c.Item1, c.Item2, c.Item3),
        })]);
        Check(cursorMissed.Count(c => c!["missed"]!.GetValue<bool>()) == 3, "three of the cursors missed rows");

        var retry = Enumerable.Range(0, 6).Select(n => (int)MailboxPace.Retry(n).TotalSeconds).ToArray();
        Check(retry.SequenceEqual([30, 60, 120, 240, 300, 300]), "reconnects wait 30 s, doubling to 5 minutes");
        var pace = new JsonObject
        {
            ["frames"] = new JsonObject { ["size"] = MailboxPace.Worker.Frames, ["everyMs"] = (long)MailboxPace.Worker.FrameEvery.TotalMilliseconds },
            ["puts"] = new JsonObject { ["size"] = MailboxPace.Worker.Puts, ["everyMs"] = (long)MailboxPace.Worker.PutEvery.TotalMilliseconds },
            ["clientEvery"] = "the Worker's every * 1.1 + 50 ms, counted from the challenge frame, a token taken at each send",
            ["retrySeconds"] = new JsonArray([.. retry.Select(s => (JsonNode)s)]),
        };

        return new JsonObject
        {
            ["boxId"] = boxId,
            ["epoch"] = 1,
            ["app"] = "launcher",
            ["batch"] = Text(batch),
            ["nonce"] = H(Scalar("frames nonce")[..12]),
            ["signerPublicKey"] = H(writer.PublicKey),
            ["envelope"] = H(envelope),
            ["frames"] = frames,
            ["framesAbout"] = "ready: first 6, last 10; stored, message and caughtUp: seq 11; message's at: 1761000000; since and trim: seq 10",
            ["cursorMissed"] = cursorMissed,
            ["pace"] = pace,
            ["split"] = SplitSection(writerId),
        };
    }

    /// <summary>The greedy split (§2.7): items as they are written, then the batches they go in and the one left out.
    /// An item's body is a string of "x" <c>bodyChars</c> long.</summary>
    private static JsonObject SplitSection(string device)
    {
        var frame = CanonicalJson.Serialize(new JsonObject { ["v"] = 1, ["app"] = "launcher", ["items"] = new JsonArray() }).Length;
        SyncBatch.Item Sized(string id, int bytes)
        {
            var bare = new SyncBatch.Item("big", id, 1_761_000_000_000, device, JsonValue.Create(""));
            return bare with { Body = JsonValue.Create(new string('x', bytes - CanonicalJson.Serialize(SyncBatch.Json(bare)).Length)) };
        }
        var half = (Envelope.MaxData - frame - 1) / 2;
        SyncBatch.Item[] items =
        [
            Sized("a", 200),
            Sized("over", Envelope.MaxData - frame + 1),
            Sized("exact", Envelope.MaxData - frame),
            Sized("pair-1", half),
            Sized("pair-2", Envelope.MaxData - frame - 1 - half),
            Sized("pair-3", half),
            Sized("pair-4", Envelope.MaxData - frame - half),
        ];
        var (batches, skipped) = SyncBatch.Split("launcher", items);
        string[][] expected = [["a"], ["exact"], ["pair-1", "pair-2"], ["pair-3"], ["pair-4"]];
        Check(batches.Select(b => b.Items.Select(i => i.Id).ToArray()).SequenceEqual(expected, new ArrayEquals()), "the split packs greedily in order");
        Check(skipped.Select(i => i.Id).SequenceEqual(["over"]), "the item one byte too big stays back alone");
        Check(batches[1].Data.Length == Envelope.MaxData && batches[2].Data.Length == Envelope.MaxData, "an envelope filled exactly holds");
        return new JsonObject
        {
            ["maxBytes"] = Envelope.MaxData,
            ["frameBytes"] = frame,
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
            {
                ["type"] = i.Type, ["id"] = i.Id, ["modified"] = i.Modified, ["device"] = i.Device,
                ["bodyChars"] = i.Body!.GetValue<string>().Length, ["bytes"] = CanonicalJson.Serialize(SyncBatch.Json(i)).Length,
            })]),
            ["batches"] = new JsonArray([.. batches.Select(b => (JsonNode)new JsonObject
            {
                ["ids"] = new JsonArray([.. b.Items.Select(i => (JsonNode)i.Id)]), ["bytes"] = b.Data.Length, ["sha256"] = H(SHA256.HashData(b.Data)),
            })]),
            ["skipped"] = new JsonArray("over"),
        };
    }

    private sealed class ArrayEquals : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(string[] obj) => obj.Length;
    }

    // Pillow 12's WebP encoder, 256 x 256 (and one 255 x 255): a solid lossless, a lossy one, one with alpha (VP8X), and a
    // two-frame animation (VP8X with its animation flag), which the profile refuses.
    private const string WebPLossless = "5249464624000000574542505650384c180000002fffc03f000750ebf694b1ff010045faff9f22fa9ffadf7f";
    private const string WebPLossy = "52494646ee0000005745425056503820e20000009011009d012a000100013ed168b153282624a2a02801001a09696ee176b103002000acddaf172721efb64e447c47df79a3c96a143992ed78b939c445c9c87bed98cb4f6c9c87bed9390f7db2721efb64e43df6c9c87d0be976bc5c9c87d0be976bc5c9c87bed9390f7db281978b9390f7db2721efb64e43df6c9c87bed9390fa17d2ed78b9390f7db2721efb64e43df6c9c87beb0000fefed6d73a344b7fff0ad7fb03fe25b2b111ffd045bf36a4fead099524fead0995247f90912fe84fa54f92e9374f0b11c4844cb3b7bf13eb550466006b4464baf880377d325d7c4000000000";
    private const string WebPExtended = "524946460401000057454250565038580a00000010000000ff0000ff0000414c504812000000010750c088080028d2ffff14d1ffd4fffe0356503820cc0000003011009d012a000100013ed168b153282624a2a02801001a09696ee176b10d0009ec03df6c9c87bed98cb4f8be87ef01d27ca289c87bed9390f7db2721f01fa5daf172721efb64e43df6c9c87bed9390f7db273888b9390f7db2721efb64e43df6c9c87bed9390f80fd2ed78b9390f7db2721efb64e43df6c9c87bed940cbc5c9c87bed9390f7db2721efb64e43df6c9c7f000fefcb37fe0d9229b91ffff10e7f815fe95b0e8a8e8334f3c334f3c334f3b1030289048c59078229c642070961a10384b0d081c258684000000";
    private const string WebPSmall = "5249464624000000574542505650384c180000002ffe803f000750ebf694b1ff010045faff9f22fa9ffaff7b";
    private const string WebPAnimated = "524946468400000057454250565038580a00000002000000ff0000ff0000414e494d06000000000000000000414e4d462a000000000000000000ff0000ff0000640000025650384c110000002fffc03f000750ebf694b1ff8188e87f0000414e4d4626000000000000000000ff0000ff0000640000005650384c0e0000002fffc03f00071011fd0f4444ff03";

    /// <summary>
    /// /7, the profile's items (plan §2.17; Exo.Identity's ProfileItems): the name, the picture (a still 256 px WebP of
    /// at most 48,000 bytes, judged by its header), a device-list change as the item that carries it, the recovery box's
    /// key, a profile batch, and what each refuses.
    /// </summary>
    /// <summary>A lossless WebP's header with this canvas, all ProfileItems reads (drawing it is the page's).</summary>
    private static byte[] LosslessHeader(int width, int height)
    {
        byte[] vp8l = [0x2f, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BitConverter.TryWriteBytes(vp8l.AsSpan(1), (uint)(width - 1) | (uint)(height - 1) << 14);
        byte[] file = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "VP8L"u8, (byte)vp8l.Length, 0, 0, 0, .. vp8l];
        BitConverter.TryWriteBytes(file.AsSpan(4), (uint)(file.Length - 8));
        return file;
    }

    private static JsonObject ProfileSection(byte[] writerKey, JsonObject firstChange)
    {
        var writerId = Device.IdOf(writerKey);
        const long T = 1_761_000_000_000;
        var name = new SyncBatch.Item(ProfileItems.ProfileType, ProfileItems.NameId, T, writerId, ProfileItems.NameBody("Erix 🎮"));
        Check(ProfileItems.Name(name) == "Erix 🎮", "a name reads back");
        string[] badNames = [" Erix", "Erix ", "E​rix", "", new string('e', 65)];
        foreach (var bad in badNames) Check(!Device.NameOk(bad) && !ProfileItems.Valid(name with { Body = JsonValue.Create(bad) }), "a name refused: " + bad);

        var lossless = Convert.FromHexString(WebPLossless);
        byte[] Padded(int length)
        {
            var bytes = new byte[length];
            lossless.CopyTo(bytes, 0);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)(length - 8));
            return bytes;
        }
        var accepted = new (string Name, byte[] Bytes)[] { ("lossless", lossless), ("lossy", Convert.FromHexString(WebPLossy)), ("extended", Convert.FromHexString(WebPExtended)), ("lossless padded to 48000", Padded(ProfileItems.MaxPictureBytes)) };
        var wrongSize = (byte[])lossless.Clone();
        wrongSize[4]++;
        var refused = new (string Why, byte[] Bytes)[]
        {
            ("255 by 255", Convert.FromHexString(WebPSmall)),
            ("animated (VP8X flag 0x02)", Convert.FromHexString(WebPAnimated)),
            ("the RIFF size isn't the file less 8 bytes", wrongSize),
            ("over 48,000 bytes (lossless padded to 48001)", Padded(ProfileItems.MaxPictureBytes + 1)),
            ("not a WebP (a PNG's signature)", [0x89, .. "PNG"u8, 0x0d, 0x0a, 0x1a, 0x0a, .. new byte[40]]),
        };
        foreach (var (what, bytes) in accepted) Check(ProfileItems.IsPicture(bytes), "a picture taken: " + what);
        foreach (var (why, bytes) in refused) Check(!ProfileItems.IsPicture(bytes), "a picture refused: " + why);
        var picture = new SyncBatch.Item(ProfileItems.ProfileType, ProfileItems.PictureId, T + 1, writerId, ProfileItems.PictureBody(lossless));
        Check(ProfileItems.Picture(picture) is { } back && back.SequenceEqual(lossless), "the picture reads back");
        var wide = LosslessHeader(ProfileItems.BannerWidth, ProfileItems.BannerHeight);
        var banner = new SyncBatch.Item(ProfileItems.ProfileType, ProfileItems.BannerId, T + 5, writerId, ProfileItems.BannerBody(wide));
        Check(ProfileItems.Banner(banner) is { } bannerBack && bannerBack.SequenceEqual(wide), "the banner reads back");
        var bannersRefused = new (string Why, byte[] Bytes)[] { ("960 by 321", LosslessHeader(960, 321)), ("the 256 by 256 picture", lossless) };
        foreach (var (why, bytes) in bannersRefused) Check(!ProfileItems.IsBanner(bytes), "a banner refused: " + why);
        Check(!ProfileItems.IsPicture(wide), "a banner isn't a picture");

        var (changeId, changeBody) = ProfileItems.DeviceChange(firstChange);
        var change = new SyncBatch.Item(ProfileItems.DeviceChangeType, changeId, T + 2, writerId, changeBody);
        Check(ProfileItems.Change(change) is not null && ProfileItems.Change(change with { Id = changeId[..^1] + (changeId[^1] == '0' ? '1' : '0') }) is null,
            "a change is carried under its own hash and no other");
        var recoveryKey = P256.DeriveScalar(Scalar("recovery box at rest"), "box"u8);
        var recovery = new SyncBatch.Item(ProfileItems.RecoveryBoxType, ProfileItems.RecoveryBoxId, T + 3, writerId, ProfileItems.RecoveryBoxBody(1, recoveryKey));
        Check(ProfileItems.RecoveryBox(recovery) is (1, var key) && key.SequenceEqual(recoveryKey), "the recovery box reads back");
        var recoveryOff = recovery with { Modified = T + 4, Body = null };

        var batch = SyncBatch.Write(ProfileItems.App, [name, picture, change, recoveryOff]);
        Check(SyncBatch.Read(batch, ProfileItems.App) is { Count: 4 } items && items.All(ProfileItems.Valid), "the profile batch reads back, every item valid");
        var unknown = new SyncBatch.Item("showcase", "games", T, writerId, JsonValue.Create("later"));
        var badKnown = new (string Why, SyncBatch.Item Item)[]
        {
            ("a profile id this version doesn't know", new(ProfileItems.ProfileType, "theme", T, writerId, JsonValue.Create("x"))),
            ("a banner with the picture's fields", banner with { Body = new JsonObject { ["data"] = Base64Url(wide), ["format"] = "webp", ["size"] = 256 } }),
            ("a picture that isn't one", picture with { Body = new JsonObject { ["data"] = "AAAA", ["format"] = "webp", ["size"] = 256 } }),
            ("a picture with a field more", picture with { Body = new JsonObject { ["data"] = Base64Url(lossless), ["format"] = "webp", ["size"] = 256, ["alt"] = "me" } }),
            ("a recovery box numbered 0", recovery with { Body = new JsonObject { ["code"] = 0, ["key"] = Base64Url(recoveryKey) } }),
            ("a device change deleted", change with { Body = null }),
        };
        foreach (var (why, item) in badKnown) Check(!ProfileItems.Valid(item), "a profile item refused: " + why);
        Check(ProfileItems.Valid(unknown), "an item of a type this version doesn't know is kept and left alone");
        return new JsonObject
        {
            ["app"] = ProfileItems.App,
            ["maxPictureBytes"] = ProfileItems.MaxPictureBytes,
            ["name"] = Text(CanonicalJson.Serialize(SyncBatch.Json(name))),
            ["namesRefused"] = new JsonArray([.. badNames.Select(n => (JsonNode)n)]),
            ["picturesTaken"] = new JsonArray([.. accepted.Take(3).Select(a => (JsonNode)new JsonObject { ["what"] = a.Name, ["webp"] = H(a.Bytes) }),
                new JsonObject { ["what"] = accepted[3].Name, ["bytes"] = ProfileItems.MaxPictureBytes }]),
            ["picturesRefused"] = new JsonArray([.. refused.Select(r => (JsonNode)(r.Bytes.Length > 1000
                ? new JsonObject { ["why"] = r.Why, ["bytes"] = r.Bytes.Length }
                : new JsonObject { ["why"] = r.Why, ["webp"] = H(r.Bytes) }))]),
            ["picture"] = Text(CanonicalJson.Serialize(SyncBatch.Json(picture))),
            ["bannerSize"] = new JsonObject { ["width"] = ProfileItems.BannerWidth, ["height"] = ProfileItems.BannerHeight },
            ["banner"] = Text(CanonicalJson.Serialize(SyncBatch.Json(banner))),
            ["bannersRefused"] = new JsonArray([.. bannersRefused.Select(r => (JsonNode)new JsonObject { ["why"] = r.Why, ["webp"] = H(r.Bytes) })]),
            ["deviceChange"] = Text(CanonicalJson.Serialize(SyncBatch.Json(change))),
            ["recoveryBox"] = Text(CanonicalJson.Serialize(SyncBatch.Json(recovery))),
            ["batch"] = Text(batch),
            ["itemsRefused"] = new JsonArray([.. badKnown.Select(b => (JsonNode)new JsonObject { ["why"] = b.Why, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(b.Item))) })]),
            ["itemKept"] = Text(CanonicalJson.Serialize(SyncBatch.Json(unknown))),
        };
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    /// <summary>
    /// /8, §2.7's rules 6 to 8 on an identity of their own (A starts it and adds B and C; D is the PC that joins, X a
    /// stranger): rule 7's new sync key in the old box and what refuses it, rule 8's three link messages under the link
    /// key and what refuses them, and device-list changes carried as profile items taken in every arrival order, a
    /// change before its predecessor retried after it.
    /// </summary>
    private static JsonObject SyncRulesSection(Signatures signed, byte[] linkKey)
    {
        const long T = 1_761_100_000_000;
        VectorSigner Key(string name) => new(P256.Signer(Scalar("rules " + name)), signed);
        using var a = Key("a");
        using var b = Key("b");
        using var c = Key("c");
        using var d = Key("d");
        using var x = Key("x");
        using var cEnc = new EcdhDecrypter(P256.Agreement(Scalar("rules c encryption")));
        byte[] Enc(string name) { using var key = P256.Agreement(Scalar("rules " + name + " encryption")); return P256.PublicKey(key); }
        var list = DeviceList.Start(a, Device.Create(a, Enc("a"), "Desk", ["launcher"], T), T);
        list.Add(a, Device.Create(b, Enc("b"), "Laptop", ["cinema"], T + 100), T + 100);
        list.Add(a, Device.Create(c, cEnc.PublicKey, "Tower", ["browser"], T + 200), T + 200);
        var dRecord = Device.Create(d, Enc("d"), "Living room", ["launcher"], T + 300);
        string Id(IDeviceSigner key) => Device.IdOf(key.PublicKey);

        // Rule 7: the remover leaves the new key for C in the old box.
        var oldKey = Scalar("rules old sync key");
        var oldBoxId = SyncSession.BoxIdOf(oldKey);
        var oldBox = Convert.FromHexString(oldBoxId);
        var kit = new RecoveryCode.Kit(list.ExoId, 4, Scalar("rules new sync key"));
        byte[] Handoff(string ephemeral, Func<ECDiffieHellman, byte[]> seal)
        {
            using var key = P256.Agreement(Scalar("rules ephemeral " + ephemeral));
            return seal(key);
        }
        var accepted = Handoff("accepted", e => SyncKeyHandoff.Seal(kit, oldBox, cEnc.PublicKey, a, e));
        Check(Envelope.Header(accepted) is (Envelope.ToDeviceKind, 0), "a handoff's header epoch is 0");
        Check(SyncKeyHandoff.Open(accepted, oldBox, cEnc, list, list.ExoId, 3) is { Epoch: 4 } opened && opened.SyncKey.SequenceEqual(kit.SyncKey), "C takes the new key");
        var refusedHandoffs = new (string Why, RecoveryCode.Kit Kit, string By, byte[] Envelope)[]
        {
            ("by isn't in the list", kit, Id(x), Handoff("stranger", e => SyncKeyHandoff.Seal(kit, oldBox, cEnc.PublicKey, x, e))),
            ("by isn't the signer (says A, signed by B)", kit, Id(a), Handoff("not signer", e => Envelope.SealToDevice(oldBox, cEnc.PublicKey, RecoveryCode.KitJson(kit, Id(a)), b, e)!)),
            ("another identity's Exo ID", kit with { ExoId = Device.ExoIdOf(x.PublicKey) }, Id(a), Handoff("exo id", e => SyncKeyHandoff.Seal(kit with { ExoId = Device.ExoIdOf(x.PublicKey) }, oldBox, cEnc.PublicKey, a, e))),
            ("an epoch not past the current 3", kit with { Epoch = 3 }, Id(a), Handoff("epoch", e => SyncKeyHandoff.Seal(kit with { Epoch = 3 }, oldBox, cEnc.PublicKey, a, e))),
        };
        foreach (var (why, _, _, envelope) in refusedHandoffs) Check(SyncKeyHandoff.Open(envelope, oldBox, cEnc, list, list.ExoId, 3) is null, "a handoff refused: " + why);
        var handoff = new JsonObject
        {
            ["changes"] = new JsonArray([.. list.Changes.Select(change => change.DeepClone())]),
            ["oldSyncKey"] = H(oldKey),
            ["oldBoxId"] = oldBoxId,
            ["currentEpoch"] = 3,
            ["recipientScalar"] = H(Scalar("rules c encryption")),
            ["recipientPublicKey"] = H(cEnc.PublicKey),
            ["hpkeInfo"] = "exo-env/1 to-device",
            ["kit"] = Text(RecoveryCode.KitJson(kit, Id(a))),
            ["ephemeralScalar"] = H(Scalar("rules ephemeral accepted")),
            ["envelope"] = H(accepted),
            ["refused"] = new JsonArray([.. refusedHandoffs.Select(r => (JsonNode)new JsonObject
            {
                ["why"] = r.Why, ["kit"] = Text(RecoveryCode.KitJson(r.Kit, r.By)), ["envelope"] = H(r.Envelope),
            })]),
        };

        // Rule 8: offer, device, added, each canonical in a 0x04 envelope under the link key.
        var withD = DeviceList.Load(list.Changes)!;
        var add = withD.Add(a, dRecord, T + 300);
        var offer = LinkFlow.OfferJson(new(list.ExoId, 3, oldKey, list.Changes));
        var device = LinkFlow.DeviceJson(dRecord);
        var added = LinkFlow.AddedJson(add);
        byte[] Sealed(string nonce, byte[] data) => Envelope.SealAes(Envelope.LinkKind, 0, new byte[16], linkKey, data, Scalar("rules link nonce " + nonce)[..12]);
        var messages = new (string Name, JsonObject Json)[] { ("offer", offer), ("device", device), ("added", added) };
        var linkMessages = new JsonArray();
        foreach (var (name, json) in messages)
        {
            var envelope = Sealed(name, CanonicalJson.Serialize(json));
            Check(LinkFlow.ReadSealed(envelope, linkKey) is { } back && JsonNode.DeepEquals(back, json), "a link message opens: " + name);
            linkMessages.Add(new JsonObject { ["t"] = name, ["json"] = Text(CanonicalJson.Serialize(json)), ["nonce"] = H(Scalar("rules link nonce " + name)[..12]), ["envelope"] = H(envelope) });
        }
        Check(LinkFlow.ReadOffer(offer) is { Epoch: 3 } read && read.Changes.Count == 3 && DeviceList.Load(read.Changes)?.ExoId == list.ExoId, "the offer reads back");
        Check(LinkFlow.ReadDevice(device) is not null && LinkFlow.ReadAdded(added) is not null, "device and added read back");
        Check(DeviceList.Load(list.Changes)!.Take(add).Result == DeviceList.Result.Appended, "N takes E's add on top of the offered chain");
        JsonObject With(JsonObject json, string key, JsonNode value) { var more = (JsonObject)json.DeepClone(); more[key] = value; return more; }
        var refusedMessages = new (string Why, byte[] Data, Func<JsonObject?, bool> Refused)[]
        {
            ("an offer with an unknown field", CanonicalJson.Serialize(With(offer, "relay", "x")), j => LinkFlow.ReadOffer(j) is null),
            ("a device message with an unknown field", CanonicalJson.Serialize(With(device, "name", "x")), j => LinkFlow.ReadDevice(j) is null),
            ("an added message with an unknown field", CanonicalJson.Serialize(With(added, "at", 1)), j => LinkFlow.ReadAdded(j) is null),
            ("a device message whose record isn't signed by its own key", CanonicalJson.Serialize(With(device, "record", With(dRecord, "name", "Den"))), j => LinkFlow.ReadDevice(j) is null),
            ("an offer not in canonical form", [.. Encoding.UTF8.GetBytes("{ " + Text(CanonicalJson.Serialize(offer))[1..])], j => j is null),
        };
        var linkRefused = new JsonArray();
        foreach (var (why, data, refused) in refusedMessages)
        {
            var envelope = Sealed("refused " + why, data);
            Check(refused(LinkFlow.ReadSealed(envelope, linkKey)), "a link message refused: " + why);
            linkRefused.Add(new JsonObject { ["why"] = why, ["data"] = Text(data), ["nonce"] = H(Scalar("rules link nonce refused " + why)[..12]), ["envelope"] = H(envelope) });
        }

        // Replication: device-list changes as profile items, taken in every arrival order.
        var chain = DeviceList.Load(list.Changes)!;
        var linear = new[]
        {
            chain.Add(a, dRecord, T + 300),
            chain.Rename(d, Id(d), "Media room", T + 400),
            chain.Remove(b, Id(c), T + 500),
        };
        var forkBase = DeviceList.Load(list.Changes)!;
        var f1 = DeviceList.Load(list.Changes)!.Rename(a, Id(b), "Laptop 2", T + 600);
        var f2 = DeviceList.Load(list.Changes)!.Rename(c, Id(c), "Tower 2", T + 600);
        var winner = forkBase.Winner([f1, f2])!;
        var afterFork = DeviceList.Load(list.Changes)!;
        afterFork.Take(winner);
        var f3 = afterFork.Rename(b, Id(a), "Desk 2", T + 700);
        JsonObject Set(string rule, JsonObject[] changes)
        {
            var orders = new JsonArray();
            string? head = null;
            DeviceList? last = null;
            foreach (var order in Orders(Enumerable.Range(0, changes.Length).ToArray()))
            {
                var taker = DeviceList.Load(list.Changes)!;
                var pending = new Queue<int>(order);
                var takes = new JsonArray();
                for (var tries = 0; pending.Count > 0; tries++)
                {
                    Check(tries < 50, "every change lands: " + rule);
                    var index = pending.Dequeue();
                    var item = ProfileItems.DeviceChange(changes[index]);
                    var result = taker.Take(ProfileItems.Change(new(ProfileItems.DeviceChangeType, item.Id, T, Id(a), item.Body))!).Result;
                    Check(result != DeviceList.Result.Invalid, "no change is refused: " + rule);
                    takes.Add(new JsonObject { ["change"] = index, ["result"] = result.ToString() });
                    if (result == DeviceList.Result.Gap) pending.Enqueue(index);
                }
                Check(head is null || head == taker.Head, "every order ends on the same head: " + rule);
                head = taker.Head;
                last = taker;
                orders.Add(new JsonObject { ["arrival"] = new JsonArray([.. order.Select(i => (JsonNode)i)]), ["takes"] = takes, ["head"] = taker.Head });
            }
            return new JsonObject
            {
                ["rule"] = rule,
                ["changes"] = new JsonArray([.. changes.Select(change => change.DeepClone())]),
                ["items"] = new JsonArray([.. changes.Select(change => (JsonNode)Text(CanonicalJson.Serialize(SyncBatch.Json(new(ProfileItems.DeviceChangeType, ProfileItems.DeviceChange(change).Id, T, Id(a), change)))))]),
                ["orders"] = orders,
                ["finalHead"] = head,
                ["devicesAtEnd"] = new JsonArray([.. last!.Devices.OrderBy(dev => dev.Id, StringComparer.Ordinal).Select(dev => (JsonNode)new JsonObject { ["id"] = dev.Id, ["name"] = dev.Name })]),
            };
        }
        var linearSet = Set("a chain of three: A adds D, D renames itself, B removes C; a change before its predecessor is a Gap, taken again after it", linear);
        Check(linearSet["finalHead"]!.GetValue<string>() == CanonicalJson.Hash(linear[2]), "the chain ends on its last change");
        var forkSet = Set("a fork (A renames B, C renames itself, at once: the lower hash wins) and a change by B on the winner", [f1, f2, f3]);
        Check(forkSet["finalHead"]!.GetValue<string>() == CanonicalJson.Hash(f3), "the change on the winner ends it");

        return new JsonObject
        {
            ["identity"] = new JsonObject
            {
                ["changes"] = new JsonArray([.. list.Changes.Select(change => change.DeepClone())]),
                ["ids"] = new JsonObject { ["a"] = Id(a), ["b"] = Id(b), ["c"] = Id(c), ["d"] = Id(d), ["x"] = Id(x) },
            },
            ["keyHandoff"] = handoff,
            ["linkKeyIs"] = "link.key",
            ["linkMessages"] = linkMessages,
            ["linkRefused"] = linkRefused,
            ["replication"] = new JsonArray(linearSet, forkSet),
        };
    }

    /// <summary>
    /// /9, §2.18's one take under the mutex (IdentityFolder.TakeChanges) on an identity of its own: A starts it and adds
    /// B, and A is this PC, with two Exo apps on one identity folder. A's rename loses a fork to B's. The first app takes
    /// B's change and signs A's rename again on top, once; the second, still holding the list it read before, takes the
    /// same change, finds that re-sign in device-list.json and signs nothing. Then B's next two changes arrive the wrong
    /// way round and both land in one take. device-list.json is given before and after each step.
    /// </summary>
    private static JsonObject TakeChangesSection(Signatures signed)
    {
        const long T = 1_761_200_000_000;
        VectorSigner Key(string name) => new(P256.Signer(Scalar("take " + name)), signed);
        byte[] Enc(string name) { using var key = P256.Agreement(Scalar("take " + name + " encryption")); return P256.PublicKey(key); }
        static string Id(IDeviceSigner key) => Device.IdOf(key.PublicKey);
        static JsonArray Changes(IEnumerable<JsonObject> changes) => new([.. changes.Select(change => change.DeepClone())]);
        using var a = Key("a");
        using var b = Key("b");
        var start = DeviceList.Start(a, Device.Create(a, Enc("a"), "Desk", ["launcher", "cinema"], T), T);
        start.Add(a, Device.Create(b, Enc("b"), "Laptop", ["launcher"], T + 100), T + 100);
        DeviceList Copy() => DeviceList.Load(start.Changes)!;

        // B renames itself, and A itself at once, A's time moved until its hash is the higher one: A's rename loses.
        var fromB = Copy().Rename(b, Id(b), "Laptop 2", T + 200);
        var at = T + 200;
        while (string.CompareOrdinal(CanonicalJson.Hash(Copy().Rename(a.Unrecorded, Id(a), "Desk 2", at)), CanonicalJson.Hash(fromB)) < 0) at++;
        var fromA = Copy().Rename(a, Id(a), "Desk 2", at);
        Check(CanonicalJson.Hash(Copy().Winner([fromA, fromB])!) == CanonicalJson.Hash(fromB), "B's rename wins the fork");
        var mine = Copy();
        mine.Take(fromA);
        var before = IdentityFiles.DeviceListFile(mine);

        var folder = Path.Combine(TestDataDirectory.Root, "vectors-take-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, IdentityFiles.DeviceList);
            File.WriteAllBytes(path, before);
            IdentityFolder.State Read() => new(start.ExoId, new DeviceKeys(Key("a"), new EcdhDecrypter(P256.Agreement(Scalar("take a encryption")))),
                IdentityFiles.ReadDeviceList(File.ReadAllBytes(path))!, 1, new byte[32], null);
            var (launcher, other) = (new IdentityFolder(folder, new TestKeys.Store()), new IdentityFolder(folder, new TestKeys.Store()));

            // Both apps read the folder before either takes anything.
            using var inLauncher = Read();
            using var inOther = Read();
            var first = launcher.TakeChanges(inLauncher, [fromB]);
            Check(first.Resigned is [var again] && SameAction(again, fromA) && Device.Text(again, "prev") == CanonicalJson.Hash(fromB) && !first.Removed,
                "the first app signs A's rename again once, on top of B's");
            var afterFirst = File.ReadAllBytes(path);
            var second = other.TakeChanges(inOther, [fromB]);
            Check(second.Resigned.Count == 0 && second.List.Head == first.List.Head && !second.Removed, "the second app finds the re-sign on disk and signs nothing");
            var afterSecond = File.ReadAllBytes(path);
            Check(afterSecond.AsSpan().SequenceEqual(afterFirst), "the second take leaves device-list.json as the first wrote it");

            var chain = DeviceList.Load(first.List.Changes)!;
            JsonObject[] later = [chain.Rename(b, Id(b), "Laptop 3", T + 400), chain.Rename(b, Id(a), "Desk 3", T + 500)];
            using var inThird = Read();
            var third = launcher.TakeChanges(inThird, [later[1], later[0]]);
            Check(third.Resigned.Count == 0 && third.List.Head == CanonicalJson.Hash(later[1]) && !third.Removed, "both of B's later changes land in one take");
            var afterThird = File.ReadAllBytes(path);

            JsonObject Step(string app, string read, JsonObject[] takes, IReadOnlyList<JsonObject> resigned, byte[] after) => new()
            {
                ["app"] = app, ["listRead"] = read, ["takes"] = Changes(takes), ["resigned"] = Changes(resigned), ["removed"] = false, ["after"] = Text(after),
            };
            return new JsonObject
            {
                ["take"] = "one hold of stateMutex: take device-list.json's changes into the list the app read, then the profile's changes in seq order (one that comes before the change it follows is tried again once that one is in), sign this PC's own changes that lost a fork again (passing over an action already on the chain), write device-list.json, let go",
                ["stateMutex"] = IdentityFiles.StateMutex,
                ["ids"] = new JsonObject { ["a"] = Id(a), ["b"] = Id(b) },
                ["thisPc"] = Id(a),
                ["thisPcScalar"] = H(Scalar("take a")),
                ["fork"] = new JsonObject { ["a"] = fromA.DeepClone(), ["b"] = fromB.DeepClone(), ["winner"] = "b" },
                ["before"] = Text(before),
                ["steps"] = new JsonArray(
                    Step("first", "before", [fromB], first.Resigned, afterFirst),
                    Step("second", "before", [fromB], second.Resigned, afterSecond),
                    Step("first", "the second step's after", [later[1], later[0]], third.Resigned, afterThird)),
                ["compare"] = "A port's own re-sign has another signature: compare the first step's resigned and after without each change's sig, and verify the sig. The later steps take the file's bytes further: compare them whole.",
            };
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
    }

    /// <summary>
    /// /12, the Exo account's entry (§2.17) on an identity of its own: A starts it, adds B and the account entry E (made
    /// from a secret, as a recovery code is: "exo vectors/account secret"'s first 16 bytes), E adds C, C adds D, and A adds
    /// G after them. C and D are restricted: each removes and renames only itself and restricted PCs added after it (never
    /// G, the owner's, though it came later), and adds no entry; E only adds PCs.
    /// Then changes after that head, each refused or taken, with the devices and the restricted PCs after it.
    /// </summary>
    private static JsonObject AccountEntrySection(Signatures signed)
    {
        const long T = 1_761_300_000_000;
        VectorSigner Key(string name) => new(P256.Signer(Scalar("account " + name)), signed);
        byte[] Enc(string name) { using var key = P256.Agreement(Scalar("account " + name + " encryption")); return P256.PublicKey(key); }
        static string Id(IDeviceSigner key) => Device.IdOf(key.PublicKey);
        using var a = Key("a");
        using var b = Key("b");
        using var c = Key("c");
        using var d = Key("d");
        using var f = Key("f");
        using var g = Key("g");
        var secretText = RecoveryCode.Format(Scalar("account secret")[..16]);
        var secret = RecoveryCode.Read(secretText)!;
        using var e = new VectorSigner(P256.Signer(secret.SignScalar), signed);
        var otherCode = RecoveryCode.Read(RecoveryCode.Format(Scalar("account other code")[..16]))!;
        using var r = new VectorSigner(P256.Signer(otherCode.SignScalar), signed);
        JsonObject Pc(VectorSigner key, string name, long at) => Device.Create(key, Enc(name), name, ["launcher"], at);

        var start = DeviceList.Start(a, Pc(a, "A", T), T);
        start.Add(a, Pc(b, "B", T + 100), T + 100);
        start.Add(a, Device.Create(e, secret.SealPublicKey, "Exo account", [], T + 200, account: true), T + 200);
        start.Add(e, Pc(c, "C", T + 300), T + 300);
        start.Add(c, Pc(d, "D", T + 400), T + 400);
        start.Add(a, Pc(g, "G", T + 450), T + 450);
        DeviceList Copy() => DeviceList.Load(start.Changes)!;
        string[] Restricted(DeviceList list) => [.. list.Devices.Where(x => list.IsRestricted(x.Id)).Select(x => x.Id).Order(StringComparer.Ordinal)];
        Check(start.AccountNumber == 1 && start.CodeNumber is null, "the account entry is the first, and recovery is off");
        Check(Restricted(start).SequenceEqual(new[] { Id(c), Id(d) }.Order(StringComparer.Ordinal)), "C and D are restricted, A and B aren't");

        JsonObject Signed(VectorSigner by, string op, JsonNode device) => CanonicalJson.Sign(new JsonObject
        {
            ["v"] = 1, ["exoId"] = start.ExoId, ["seq"] = start.Changes.Count, ["prev"] = start.Head, ["op"] = op, ["device"] = device.DeepClone(), ["by"] = Id(by), ["at"] = T + 500,
        }, DeviceList.ChangeLabel, by);
        var refused = new JsonArray();
        void Refuse(VectorSigner by, string op, JsonNode device, string why)
        {
            var change = Signed(by, op, device);
            Check(Copy().Take(change).Result == DeviceList.Result.Invalid, "refused: " + why);
            refused.Add(new JsonObject { ["change"] = change, ["why"] = why });
        }
        var accepted = new JsonArray();
        void Accept(VectorSigner by, string op, JsonNode device, string why)
        {
            var change = Signed(by, op, device);
            var list = Copy();
            Check(list.Take(change).Result == DeviceList.Result.Appended, "taken: " + why);
            accepted.Add(new JsonObject
            {
                ["change"] = change,
                ["why"] = why,
                ["devicesAfter"] = new JsonArray([.. list.Devices.Select(x => x.Id).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
                ["restrictedAfter"] = new JsonArray([.. Restricted(list).Select(id => (JsonNode)id)]),
            });
        }
        JsonObject Rename(VectorSigner who, string name) => new() { ["id"] = Id(who), ["name"] = name };
        var recoveryEntry = Device.Create(r, otherCode.SealPublicKey, "Recovery code", [], T + 500, recovery: true);
        var secondAccount = Device.Create(r, otherCode.SealPublicKey, "Exo account", [], T + 500, account: true);
        var both = CanonicalJson.Sign(new JsonObject
        {
            ["v"] = 1, ["id"] = Id(f), ["name"] = "F", ["created"] = T + 500, ["sigPub"] = System.Buffers.Text.Base64Url.EncodeToString(f.PublicKey),
            ["encPub"] = System.Buffers.Text.Base64Url.EncodeToString(Enc("F")), ["apps"] = new JsonArray(), ["recovery"] = true, ["account"] = true,
        }, Device.Label, f);
        Check(Device.Read(both.DeepClone()) is null, "a record with both flags is refused");
        var accountFalse = CanonicalJson.Sign(new JsonObject
        {
            ["v"] = 1, ["id"] = Id(f), ["name"] = "F", ["created"] = T + 500, ["sigPub"] = System.Buffers.Text.Base64Url.EncodeToString(f.PublicKey),
            ["encPub"] = System.Buffers.Text.Base64Url.EncodeToString(Enc("F")), ["apps"] = new JsonArray("launcher"), ["account"] = false,
        }, Device.Label, f);
        Check(Device.Read(accountFalse.DeepClone()) is null, "a record with account false is refused");

        Refuse(c, "remove", Id(a), "a restricted PC removes a PC added before it (A)");
        Refuse(c, "remove", Id(b), "a restricted PC removes a PC added before it (B)");
        Refuse(d, "remove", Id(c), "a restricted PC removes the restricted PC that added it");
        Refuse(c, "rename", Rename(a, "Mine now"), "a restricted PC renames a PC added before it");
        Refuse(c, "remove", Id(e), "a restricted PC removes the account entry");
        Refuse(c, "remove", Id(g), "a restricted PC removes a PC the owner added after it (not restricted)");
        Refuse(c, "rename", Rename(g, "G 2"), "a restricted PC renames a PC the owner added after it");
        Refuse(c, "add", recoveryEntry, "a restricted PC adds the recovery entry");
        Refuse(e, "remove", Id(b), "the account entry removes a device");
        Refuse(e, "remove", Id(e), "the account entry removes itself (a PC turns account sign-in off)");
        Refuse(e, "rename", Rename(c, "C 2"), "the account entry renames a device");
        Refuse(e, "add", recoveryEntry, "the account entry adds the recovery entry");
        Refuse(b, "add", secondAccount, "a PC adds a second account entry while there is one");
        Refuse(a, "add", both, "a device record with both recovery and account");
        Refuse(a, "add", accountFalse, "a device record with \"account\": false (the flag is there only as true)");

        Accept(c, "remove", Id(d), "a restricted PC removes a PC added after it");
        Accept(c, "rename", Rename(d, "D 2"), "a restricted PC renames a PC added after it");
        Accept(c, "remove", Id(c), "a restricted PC removes itself: it leaves the identity");
        Accept(b, "remove", Id(c), "a PC that isn't restricted removes a restricted one");
        Accept(a, "remove", Id(e), "a PC that isn't restricted removes the account entry: account sign-in off");
        Accept(e, "add", Pc(f, "F", T + 500), "the account entry adds a PC, which is restricted");
        Accept(d, "add", Pc(f, "F", T + 500), "a restricted PC adds a PC, which is restricted too");
        Accept(b, "add", recoveryEntry, "a PC that isn't restricted adds the recovery entry, with account sign-in on");

        return new JsonObject
        {
            ["about"] = "The account entry has \"account\": true and only adds PCs. A PC it adds is restricted, and so is any PC a restricted PC adds: it removes and renames only itself and restricted PCs whose add comes after its own (never a PC the owner brought in, however late), and adds no entry. A PC that isn't restricted adds the account entry when there is none and removes it (account sign-in off); the recovery entry removes it too. AccountNumber counts account entries ever added; its kits carry it, as CodeNumber does the recovery code's.",
            ["accountSecret"] = secretText,
            ["changes"] = new JsonArray([.. start.Changes.Select(change => change.DeepClone())]),
            ["ids"] = new JsonObject { ["a"] = Id(a), ["b"] = Id(b), ["c"] = Id(c), ["d"] = Id(d), ["e"] = Id(e), ["f"] = Id(f), ["g"] = Id(g), ["r"] = Id(r) },
            ["accountNumber"] = 1,
            ["restricted"] = new JsonArray([.. Restricted(start).Select(id => (JsonNode)id)]),
            ["refused"] = refused,
            ["accepted"] = accepted,
        };
    }

    /// <summary>
    /// /14, signing in again after leaving (§2.17): the account entry's add is restricted while a PC that isn't restricted
    /// is listed, or the recovery entry is; with neither (the owner signed out of their last PC), it isn't, as a first PC
    /// isn't. Neither entry counts as a PC, and a restricted PC's add stays restricted. A starts each identity and adds
    /// the account entry E; E adds C, and A leaves. Each case: the changes before it, the add, and who is restricted after.
    /// </summary>
    private static JsonObject RejoinSection(Signatures signed)
    {
        const long T = 1_761_500_000_000;
        VectorSigner Key(string name) => new(P256.Signer(Scalar("rejoin " + name)), signed);
        byte[] Enc(string name) { using var key = P256.Agreement(Scalar("rejoin " + name + " encryption")); return P256.PublicKey(key); }
        static string Id(IDeviceSigner key) => Device.IdOf(key.PublicKey);
        using var a = Key("a");
        using var c = Key("c");
        using var f = Key("f");
        using var h = Key("h");
        var secretText = RecoveryCode.Format(Scalar("rejoin account secret")[..16]);
        var secret = RecoveryCode.Read(secretText)!;
        using var e = new VectorSigner(P256.Signer(secret.SignScalar), signed);
        var codeText = RecoveryCode.Format(Scalar("rejoin recovery code")[..16]);
        var code = RecoveryCode.Read(codeText)!;
        using var r = new VectorSigner(P256.Signer(code.SignScalar), signed);
        JsonObject Pc(VectorSigner key, string name, long at) => Device.Create(key, Enc(name), name, ["launcher"], at);
        string[] Restricted(DeviceList list) => [.. list.Devices.Where(x => list.IsRestricted(x.Id)).Select(x => x.Id).Order(StringComparer.Ordinal)];

        // E adds C while A is listed (C restricted), then A leaves: no PC that isn't restricted is left.
        DeviceList Left(bool recovery)
        {
            var list = DeviceList.Start(a, Pc(a, "A", T), T);
            list.Add(a, Device.Create(e, secret.SealPublicKey, "Exo account", [], T + 100, account: true), T + 100);
            if (recovery) list.Add(a, Device.Create(r, code.SealPublicKey, "Recovery code", [], T + 150, recovery: true), T + 150);
            list.Add(e, Pc(c, "C", T + 200), T + 200);
            list.Remove(a, Id(a), T + 300);
            return list;
        }
        var cases = new JsonArray();
        void Case(DeviceList before, VectorSigner by, VectorSigner added, string name, bool restricted, string why)
        {
            var list = DeviceList.Load(before.Changes)!;
            var start = list.Changes.Count;
            list.Add(by, Pc(added, name, T + 500), T + 500);
            Check(list.IsRestricted(Id(added)) == restricted, why);
            cases.Add(new JsonObject
            {
                ["why"] = why,
                ["changes"] = new JsonArray([.. before.Changes.Select(x => x.DeepClone())]),
                ["add"] = list.Changes[start].DeepClone(),
                ["restrictedAfter"] = new JsonArray([.. Restricted(list).Select(id => (JsonNode)id)]),
            });
        }

        var withA = DeviceList.Start(a, Pc(a, "A", T), T);
        withA.Add(a, Device.Create(e, secret.SealPublicKey, "Exo account", [], T + 100, account: true), T + 100);
        Case(withA, e, f, "F", true, "the owner's PC A is listed: the account entry's add is restricted");
        var left = Left(recovery: false);
        Check(Restricted(left).SequenceEqual(new[] { Id(c) }), "after A leaves only C, restricted, is left with E");
        Case(left, e, f, "F", false, "no PC that isn't restricted, recovery off: the account entry's add isn't restricted");
        Case(left, c, f, "F", true, "no PC that isn't restricted: a restricted PC's add stays restricted");
        var back = DeviceList.Load(left.Changes)!;
        back.Add(e, Pc(f, "F", T + 400), T + 400);
        Case(back, e, h, "H", true, "F, the owner back, is listed: the next account add is restricted again");
        var entriesOnly = DeviceList.Load(left.Changes)!;
        entriesOnly.Remove(c, Id(c), T + 400);
        Case(entriesOnly, e, f, "F", false, "only the account entry is left: its add is like a first PC's");
        Case(Left(recovery: true), e, f, "F", true, "the recovery entry is listed: the code is the way back, so the add is restricted");

        return new JsonObject
        {
            ["about"] = "The account entry's add is restricted while a PC that isn't restricted is listed, or the recovery entry is. With neither (the owner signed out of their last PC, or reinstalled), it isn't. Neither entry counts as a PC; a restricted PC's add stays restricted. Restricted is read from the chain as merged: an add that loses a fork and is signed again on top can change.",
            ["accountSecret"] = secretText,
            ["recoveryCode"] = codeText,
            ["ids"] = new JsonObject { ["a"] = Id(a), ["c"] = Id(c), ["e"] = Id(e), ["f"] = Id(f), ["h"] = Id(h), ["r"] = Id(r) },
            ["cases"] = cases,
        };
    }

    /// <summary>
    /// /14, the profile's look (§2.17; ProfileItems.Look): looks every app takes, as items in canonical JSON, and looks
    /// every app refuses, each with why. At most 4,096 bytes of canonical JSON; media at most 200 MB.
    /// </summary>
    private static JsonObject LookSection(string writerId)
    {
        const long T = 1_761_400_000_000;
        var sha = H(SHA256.HashData("exo vectors/look media"u8));
        JsonObject Full() => new()
        {
            ["v"] = 1, ["accent"] = "#ff6a2b", ["effect"] = "embers", ["frame"] = "fire", ["name"] = "glow",
            ["showcases"] = new JsonArray("recent", "stats"),
            ["avatar"] = ProfileItems.Media(sha, "image/gif", 1_200_000),
            ["banner"] = ProfileItems.Media(sha, "image/webp", 300_000),
            ["background"] = ProfileItems.Media(sha, "video/webm", 5_000_000),
            ["later"] = "a field a newer app knows",
        };
        JsonObject With(Action<JsonObject> change) { var look = Full(); change(look); return look; }
        JsonObject Sized(int bytes)
        {
            var look = With(l => l["later"] = "");
            look["later"] = new string('x', bytes - CanonicalJson.Serialize(look).Length);
            Check(CanonicalJson.Serialize(look).Length == bytes, "a look padded to " + bytes + " bytes");
            return look;
        }
        SyncBatch.Item Item(JsonNode? body) => new(ProfileItems.ProfileType, ProfileItems.LookId, T, writerId, body);

        var taken = new (string What, JsonObject Look)[]
        {
            ("every field, and one a newer app knows", Full()),
            ("v alone", new JsonObject { ["v"] = 1 }),
            ("no showcases shown", new JsonObject { ["v"] = 1, ["showcases"] = new JsonArray() }),
            ("media of exactly 200 MB", With(l => l["background"] = ProfileItems.Media(sha, "video/mp4", ProfileItems.MaxMediaBytes))),
            ("exactly 4,096 bytes", Sized(ProfileItems.MaxLookBytes)),
        };
        var refused = new (string Why, JsonObject Look)[]
        {
            ("v 2", With(l => l["v"] = 2)),
            ("no v", With(l => l.Remove("v"))),
            ("v as text", With(l => l["v"] = "1")),
            ("an accent in capitals", With(l => l["accent"] = "#FF6A2B")),
            ("an accent by name", With(l => l["accent"] = "red")),
            ("a three-digit accent", With(l => l["accent"] = "#f60")),
            ("an effect off its list", With(l => l["effect"] = "tornado")),
            ("an empty frame", With(l => l["frame"] = "")),
            ("a name style off its list", With(l => l["name"] = "rainbow")),
            ("a showcase twice", With(l => l["showcases"] = new JsonArray("stats", "stats"))),
            ("a showcase off its list", With(l => l["showcases"] = new JsonArray("stats", "trophies"))),
            ("showcases as text", With(l => l["showcases"] = "stats")),
            ("a media type Exo doesn't play", With(l => l["avatar"] = ProfileItems.Media(sha, "image/svg+xml", 100))),
            ("a sha in capitals", With(l => l["avatar"] = ProfileItems.Media(sha.ToUpperInvariant(), "image/gif", 100))),
            ("a 63-character sha", With(l => l["avatar"] = ProfileItems.Media(sha[..63], "image/gif", 100))),
            ("media of 0 bytes", With(l => l["avatar"] = ProfileItems.Media(sha, "image/gif", 0))),
            ("media over 200 MB", With(l => l["banner"] = ProfileItems.Media(sha, "video/mp4", ProfileItems.MaxMediaBytes + 1))),
            ("media with a fourth field", With(l => l["avatar"] = new JsonObject { ["bytes"] = 100, ["sha"] = sha, ["type"] = "image/gif", ["alt"] = "me" })),
            ("4,097 bytes", Sized(ProfileItems.MaxLookBytes + 1)),
        };
        foreach (var (what, look) in taken) Check(ProfileItems.Valid(Item(look)) && ProfileItems.Look(Item(look)) is not null, "a look taken: " + what);
        foreach (var (why, look) in refused) Check(!ProfileItems.Valid(Item(look)), "a look refused: " + why);
        Check(ProfileItems.Valid(Item(null)), "a look deleted");
        return new JsonObject
        {
            ["maxLookBytes"] = ProfileItems.MaxLookBytes,
            ["maxMediaBytes"] = ProfileItems.MaxMediaBytes,
            ["mediaTypes"] = new JsonArray([.. ProfileItems.MediaTypes.Select(t => (JsonNode)t)]),
            ["effects"] = new JsonArray([.. ProfileItems.Effects.Select(t => (JsonNode)t)]),
            ["frames"] = new JsonArray([.. ProfileItems.Frames.Select(t => (JsonNode)t)]),
            ["nameStyles"] = new JsonArray([.. ProfileItems.NameStyles.Select(t => (JsonNode)t)]),
            ["showcases"] = new JsonArray([.. ProfileItems.Showcases.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Look)))) })]),
            ["deleted"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(null)))),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["look"] = x.Look.DeepClone() })]),
        };
    }

    /// <summary>
    /// /15, the profile's canvas (§2.17; ProfileItems.Canvas): canvases every app takes, as items in canonical JSON, and
    /// canvases every app refuses, each with why; with /15's additions to the look (a media's view, accent2, font).
    /// </summary>
    private static JsonObject CanvasSection(string writerId)
    {
        const long T = 1_761_600_000_000;
        var sha = H(SHA256.HashData("exo vectors/canvas media"u8));
        JsonObject Block(string id, string kind, int x, int y, int w, int h, Action<JsonObject>? more = null)
        {
            var block = new JsonObject { ["id"] = id, ["kind"] = kind, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };
            more?.Invoke(block);
            return block;
        }
        JsonObject Full() => new()
        {
            ["v"] = 1,
            ["blocks"] = new JsonArray(
                Block("stats", "showcase", 0, 0, 24, 4, b => b["showcase"] = "stats"),
                Block("track", "showcase", 0, 4, 12, 6, b => { b["showcase"] = "trackers"; b["trackers"] = new JsonArray("hours", "top-game", "pcs"); b["surface"] = "tint"; }),
                Block("shelf", "showcase", 12, 4, 12, 10, b =>
                {
                    b["showcase"] = "shelf";
                    b["shelf"] = new JsonObject
                    {
                        ["favorite"] = new JsonObject { ["game"] = "epic:Sugar", ["note"] = "Ranked every night." },
                        ["rotation"] = new JsonArray("steam:730", "riot:valorant"), ["want"] = new JsonArray(), ["finished"] = new JsonArray("steam:1145360"),
                    };
                }),
                Block("art", "media", 2, 10, 8, 8, b =>
                {
                    b["media"] = new JsonObject { ["bytes"] = 2_400_000, ["sha"] = sha, ["type"] = "video/webm", ["focus"] = new JsonArray(40, 60), ["zoom"] = 150, ["trim"] = new JsonArray(500, 4500) };
                    b["z"] = 3; b["rot"] = -8; b["opacity"] = 90; b["blend"] = "screen"; b["surface"] = "window"; b["radius"] = 24; b["frame"] = "glow";
                }),
                Block("bio", "text", 0, 18, 24, 6, b => { b["text"] = "# Hi\nI play **Rocket League**.\n||spoiler||"; b["align"] = "center"; b["size"] = "l"; }),
                Block("later", "sticker3d", 0, 24, 4, 4, b => b["model"] = "a kind a newer app knows")),
            ["later"] = "a field a newer app knows",
        };
        JsonObject With(Action<JsonObject> change) { var canvas = Full(); change(canvas); return canvas; }
        JsonObject Changed(string id, Action<JsonObject> change) => With(c => change((JsonObject)((JsonArray)c["blocks"]!).First(b => b!["id"]!.GetValue<string>() == id)!));
        JsonObject Many(int count) => new() { ["v"] = 1, ["blocks"] = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode)Block("b" + i, "showcase", 0, i, 24, 1, b => b["showcase"] = "recent"))]) };
        JsonObject Sized(int bytes)
        {
            var canvas = With(c => c["later"] = "");
            canvas["later"] = new string('x', bytes - CanonicalJson.Serialize(canvas).Length);
            Check(CanonicalJson.Serialize(canvas).Length == bytes, "a canvas padded to " + bytes + " bytes");
            return canvas;
        }
        SyncBatch.Item Item(JsonNode? body) => new(ProfileItems.ProfileType, ProfileItems.CanvasId, T, writerId, body);

        var taken = new (string What, JsonObject Canvas)[]
        {
            ("every kind and field, a block of a kind a newer app knows, and a field it knows", Full()),
            ("no blocks", new JsonObject { ["v"] = 1, ["blocks"] = new JsonArray() }),
            ("48 blocks", Many(ProfileItems.MaxBlocks)),
            ("exactly 16,384 bytes", Sized(ProfileItems.MaxCanvasBytes)),
            ("a block at the last column and row", new JsonObject { ["v"] = 1, ["blocks"] = new JsonArray(Block("edge", "text", 23, 499, 1, 120, b => b["text"] = ".")) }),
        };
        var refused = new (string Why, JsonObject Canvas)[]
        {
            ("no v", With(c => c.Remove("v"))),
            ("blocks as an object", With(c => c["blocks"] = new JsonObject())),
            ("49 blocks", Many(ProfileItems.MaxBlocks + 1)),
            ("16,385 bytes", Sized(ProfileItems.MaxCanvasBytes + 1)),
            ("one id twice", Changed("track", b => b["id"] = "stats")),
            ("an id in capitals", Changed("stats", b => b["id"] = "Stats")),
            ("an id of 13 characters", Changed("stats", b => b["id"] = "abcdefghijklm")),
            ("past the right edge", Changed("track", b => b["x"] = 13)),
            ("x 24", Changed("bio", b => { b["x"] = 24; b["w"] = 1; })),
            ("no width", Changed("bio", b => b["w"] = 0)),
            ("row 500", Changed("bio", b => b["y"] = 500)),
            ("121 rows tall", Changed("bio", b => b["h"] = 121)),
            ("no kind", Changed("bio", b => b.Remove("kind"))),
            ("z 51", Changed("art", b => b["z"] = 51)),
            ("turned 181 degrees", Changed("art", b => b["rot"] = 181)),
            ("opacity 9", Changed("art", b => b["opacity"] = 9)),
            ("a radius of 65", Changed("art", b => b["radius"] = 65)),
            ("a blend off its list", Changed("art", b => b["blend"] = "difference")),
            ("a surface off its list", Changed("art", b => b["surface"] = "mirror")),
            ("a frame off its list", Changed("art", b => b["frame"] = "neon")),
            ("a showcase off its list", Changed("stats", b => b["showcase"] = "trophies")),
            ("four trackers", Changed("track", b => b["trackers"] = new JsonArray("hours", "games", "played", "pcs"))),
            ("a tracker twice", Changed("track", b => b["trackers"] = new JsonArray("hours", "hours"))),
            ("no trackers", Changed("track", b => b["trackers"] = new JsonArray())),
            ("a tracker off its list", Changed("track", b => b["trackers"] = new JsonArray("kills"))),
            ("a favorite with no game", Changed("shelf", b => b["shelf"]!["favorite"] = new JsonObject { ["note"] = "?" })),
            ("a note of 141 characters", Changed("shelf", b => b["shelf"]!["favorite"] = new JsonObject { ["game"] = "epic:Sugar", ["note"] = new string('n', 141) })),
            ("a favorite with a field more", Changed("shelf", b => b["shelf"]!["favorite"] = new JsonObject { ["game"] = "epic:Sugar", ["stars"] = 5 })),
            ("seven in rotation", Changed("shelf", b => b["shelf"]!["rotation"] = new JsonArray([.. Enumerable.Range(0, 7).Select(i => (JsonNode)("steam:" + i))]))),
            ("thirteen finished", Changed("shelf", b => b["shelf"]!["finished"] = new JsonArray([.. Enumerable.Range(0, 13).Select(i => (JsonNode)("steam:" + i))]))),
            ("a game twice", Changed("shelf", b => b["shelf"]!["want"] = new JsonArray("steam:730", "steam:730"))),
            ("an empty game id", Changed("shelf", b => b["shelf"]!["want"] = new JsonArray(""))),
            ("a media block without its media", Changed("art", b => b.Remove("media"))),
            ("a trim on a picture", Changed("art", b => b["media"] = new JsonObject { ["bytes"] = 100, ["sha"] = sha, ["type"] = "image/png", ["trim"] = new JsonArray(0, 1000) })),
            ("a trim that ends before it starts", Changed("art", b => b["media"]!["trim"] = new JsonArray(4500, 500))),
            ("a trim past an hour", Changed("art", b => b["media"]!["trim"] = new JsonArray(0, 3_600_001))),
            ("a focus past 100", Changed("art", b => b["media"]!["focus"] = new JsonArray(101, 50))),
            ("a zoom under 100", Changed("art", b => b["media"]!["zoom"] = 99)),
            ("empty text", Changed("bio", b => b["text"] = "")),
            ("2,001 characters", Changed("bio", b => b["text"] = new string('t', ProfileItems.MaxTextChars + 1))),
            ("an alignment off its list", Changed("bio", b => b["align"] = "justify")),
            ("a size off its list", Changed("bio", b => b["size"] = "xxl")),
        };
        foreach (var (what, canvas) in taken) Check(ProfileItems.Valid(Item(canvas)) && ProfileItems.Canvas(Item(canvas)) is not null, "a canvas taken: " + what);
        foreach (var (why, canvas) in refused) Check(!ProfileItems.Valid(Item(canvas)), "a canvas refused: " + why);
        Check(ProfileItems.Valid(Item(null)), "a canvas deleted");

        // /15's look: a media's view, accent2 and font.
        SyncBatch.Item LookItem(JsonObject look) => new(ProfileItems.ProfileType, ProfileItems.LookId, T, writerId, look);
        var look = new JsonObject
        {
            ["v"] = 1, ["accent"] = "#3ee6b0", ["accent2"] = "#ff5fd2", ["font"] = "orbitron", ["name"] = "gradient", ["effect"] = "fireflies",
            ["background"] = new JsonObject { ["bytes"] = 9_000_000, ["sha"] = sha, ["type"] = "video/mp4", ["focus"] = new JsonArray(50, 30), ["zoom"] = 120, ["trim"] = new JsonArray(0, 8000) },
        };
        Check(ProfileItems.Valid(LookItem(look)), "a /15 look taken");
        var lookRefused = new (string Why, JsonObject Look)[]
        {
            ("accent2 in capitals", new JsonObject { ["v"] = 1, ["accent2"] = "#FF5FD2" }),
            ("a font off its list", new JsonObject { ["v"] = 1, ["font"] = "comic-sans" }),
        };
        foreach (var (why, bad) in lookRefused) Check(!ProfileItems.Valid(LookItem(bad)), "a /15 look refused: " + why);
        return new JsonObject
        {
            ["columns"] = ProfileItems.CanvasColumns,
            ["maxCanvasBytes"] = ProfileItems.MaxCanvasBytes,
            ["maxBlocks"] = ProfileItems.MaxBlocks,
            ["maxRows"] = ProfileItems.MaxCanvasRows,
            ["maxBlockRows"] = ProfileItems.MaxBlockRows,
            ["maxTextChars"] = ProfileItems.MaxTextChars,
            ["kinds"] = new JsonArray([.. ProfileItems.BlockKinds.Select(t => (JsonNode)t)]),
            ["surfaces"] = new JsonArray([.. ProfileItems.Surfaces.Select(t => (JsonNode)t)]),
            ["blends"] = new JsonArray([.. ProfileItems.Blends.Select(t => (JsonNode)t)]),
            ["aligns"] = new JsonArray([.. ProfileItems.TextAligns.Select(t => (JsonNode)t)]),
            ["sizes"] = new JsonArray([.. ProfileItems.TextSizes.Select(t => (JsonNode)t)]),
            ["trackers"] = new JsonArray([.. ProfileItems.Trackers.Select(t => (JsonNode)t)]),
            ["fonts"] = new JsonArray([.. ProfileItems.Fonts.Select(t => (JsonNode)t)]),
            ["effects"] = new JsonArray([.. ProfileItems.Effects.Select(t => (JsonNode)t)]),
            ["nameStyles"] = new JsonArray([.. ProfileItems.NameStyles.Select(t => (JsonNode)t)]),
            ["showcases"] = new JsonArray([.. ProfileItems.Showcases.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Canvas)))) })]),
            ["deleted"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(null)))),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["canvas"] = x.Canvas.DeepClone() })]),
            ["lookTaken"] = Text(CanonicalJson.Serialize(SyncBatch.Json(LookItem(look)))),
            ["lookRefused"] = new JsonArray([.. lookRefused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["look"] = x.Look.DeepClone() })]),
        };
    }

    /// <summary>
    /// /16, a setup block's gear (§2.17; ProfileItems.GearOk): picked from Exo's device list, each with the maker's
    /// picture as profile media; canvases every app takes, as items in canonical JSON, and canvases every app refuses.
    /// </summary>
    private static JsonObject GearSection(string writerId)
    {
        const long T = 1_761_700_000_000;
        var sha = H(SHA256.HashData("exo vectors/gear picture"u8));
        JsonObject Pick(string kind, string maker, string model, Action<JsonObject>? more = null)
        {
            var pick = new JsonObject { ["kind"] = kind, ["maker"] = maker, ["model"] = model };
            more?.Invoke(pick);
            return pick;
        }
        JsonArray Full() => new(
            Pick("mouse", "Finalmouse", "Starlight X", p => p["picture"] = new JsonObject { ["bytes"] = 180_000, ["sha"] = sha, ["type"] = "image/png" }),
            Pick("keyboard", "Wooting", "60HE v2", p => p["variant"] = "ansi"),
            Pick("audio", "ZiiGaat", "Lush", p => p["look"] = "iem"),
            Pick("controller", "Sony", "DualSense Edge", p => { p["variant"] = "Midnight Black"; p["later"] = "a field a newer app knows"; }),
            Pick("mousepad", "Superglide", "3S", p => p["variant"] = "Black"),
            Pick("microphone", "Fifine", "AM8"));
        JsonObject Canvas(JsonNode gear) => new()
        {
            ["v"] = 1,
            ["blocks"] = new JsonArray(new JsonObject { ["id"] = "setup", ["kind"] = "showcase", ["showcase"] = "setup", ["x"] = 0, ["y"] = 0, ["w"] = 24, ["h"] = 7, ["gear"] = gear }),
        };
        JsonObject Changed(int index, Action<JsonObject> change) { var gear = Full(); change((JsonObject)gear[index]!); return Canvas(gear); }
        SyncBatch.Item Item(JsonObject canvas) => new(ProfileItems.ProfileType, ProfileItems.CanvasId, T, writerId, canvas);

        var taken = new (string What, JsonObject Canvas)[]
        {
            ("every kind, a picture, a variant, a look and a field a newer app knows", Canvas(Full())),
            ("no gear", Canvas(new JsonArray())),
            ("eight pieces", Canvas(new JsonArray([.. Enumerable.Range(0, ProfileItems.MaxGear).Select(i => (JsonNode)Pick("mouse", "Razer", "Viper " + i))]))),
        };
        var refused = new (string Why, JsonObject Canvas)[]
        {
            ("nine pieces", Canvas(new JsonArray([.. Enumerable.Range(0, ProfileItems.MaxGear + 1).Select(i => (JsonNode)Pick("mouse", "Razer", "Viper " + i))]))),
            ("gear as an object", Canvas(new JsonObject())),
            ("a kind off its list", Changed(0, p => p["kind"] = "monitor")),
            ("no maker", Changed(1, p => p.Remove("maker"))),
            ("an empty model", Changed(1, p => p["model"] = "")),
            ("a maker of 41 characters", Changed(1, p => p["maker"] = new string('m', 41))),
            ("a model of 65 characters", Changed(1, p => p["model"] = new string('m', 65))),
            ("a variant of 41 characters", Changed(3, p => p["variant"] = new string('v', 41))),
            ("a control character in a name", Changed(2, p => p["model"] = "Lush\n")),
            ("a look on a mouse", Changed(0, p => p["look"] = "iem")),
            ("a look off its list", Changed(2, p => p["look"] = "earbuds")),
            ("a video as a picture", Changed(0, p => p["picture"] = new JsonObject { ["bytes"] = 180_000, ["sha"] = sha, ["type"] = "video/mp4" })),
            ("a picture without its sha", Changed(0, p => p["picture"] = new JsonObject { ["bytes"] = 180_000, ["type"] = "image/png" })),
        };
        foreach (var (what, canvas) in taken) Check(ProfileItems.Valid(Item(canvas)) && ProfileItems.Canvas(Item(canvas)) is not null, "gear taken: " + what);
        foreach (var (why, canvas) in refused) Check(!ProfileItems.Valid(Item(canvas)), "gear refused: " + why);
        return new JsonObject
        {
            ["maxGear"] = ProfileItems.MaxGear,
            ["kinds"] = new JsonArray([.. ProfileItems.GearKinds.Select(t => (JsonNode)t)]),
            ["looks"] = new JsonArray([.. ProfileItems.GearLooks.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Canvas)))) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["canvas"] = x.Canvas.DeepClone() })]),
        };
    }

    /// <summary>
    /// /16, the hero as blocks and a font on any block (§2.17; ProfileItems.Canvas): the banner, picture and name placed
    /// like any block, with the canvas's hero naming where they are; canvases every app takes and refuses.
    /// </summary>
    private static JsonObject HeroSection(string writerId)
    {
        const long T = 1_761_700_000_000;
        JsonObject Block(string id, string kind, int x, int y, int w, int h, Action<JsonObject>? more = null)
        {
            var block = new JsonObject { ["id"] = id, ["kind"] = kind, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };
            more?.Invoke(block);
            return block;
        }
        JsonObject Full() => new()
        {
            ["v"] = 1,
            ["hero"] = "blocks",
            ["blocks"] = new JsonArray(
                Block("banner", "banner", 0, 0, 24, 5, b => b["radius"] = 22),
                Block("avatar", "avatar", 0, 3, 3, 4, b => { b["z"] = 2; b["frame"] = "orbit"; }),
                Block("name", "name", 3, 5, 10, 2, b => { b["z"] = 2; b["font"] = "bebas"; }),
                Block("bio", "text", 13, 5, 11, 2, b => { b["text"] = "# 覚悟"; b["font"] = "script"; }),
                Block("stats", "showcase", 0, 7, 24, 3, b => { b["showcase"] = "stats"; b["font"] = "orbitron"; b["size"] = "s"; }),
                Block("pc", "showcase", 0, 10, 9, 6, b => { b["showcase"] = "pc"; b["build"] = new JsonArray(new JsonObject { ["part"] = "case", ["name"] = "NZXT H5 Flow" }); }),
                Block("gear", "showcase", 9, 10, 15, 8, b => { b["showcase"] = "gear"; b["size"] = "xl"; b["gear"] = new JsonArray(new JsonObject { ["kind"] = "mousepad", ["maker"] = "Superglide", ["model"] = "3S" }); })),
        };
        JsonObject With(Action<JsonObject> change) { var canvas = Full(); change(canvas); return canvas; }
        SyncBatch.Item Item(JsonObject canvas) => new(ProfileItems.ProfileType, ProfileItems.CanvasId, T, writerId, canvas);

        var taken = new (string What, JsonObject Canvas)[]
        {
            ("the hero as blocks, fonts on a name, a text and a showcase, the setup's halves on their own, and showcases sized", Full()),
            ("the hero as the strip", With(c => c["hero"] = "strip")),
            ("no hero named (the strip)", With(c => c.Remove("hero"))),
            ("a grid of 96, a block at its last column and row, as tall as it allows", new JsonObject { ["v"] = 1, ["grid"] = 96,
                ["blocks"] = new JsonArray(Block("edge", "text", 95, 1999, 1, 480, b => b["text"] = ".")) }),
            ("a grid of 24 named", With(c => c["grid"] = 24)),
        };
        var refused = new (string Why, JsonObject Canvas)[]
        {
            ("a hero place off its list", With(c => c["hero"] = "top")),
            ("a hero place that isn't a word", With(c => c["hero"] = true)),
            ("a font off its list", With(c => c["blocks"]![3]!["font"] = "comic-sans")),
            ("a size off its list", With(c => c["blocks"]![6]!["size"] = "xxl")),
            ("a grid of 48", With(c => c["grid"] = 48)),
            ("a grid of 2^32 + 24 (24 once cut to 32 bits)", With(c => c["grid"] = 4294967320L)),
            ("past the right edge of a 96 grid", new JsonObject { ["v"] = 1, ["grid"] = 96, ["blocks"] = new JsonArray(Block("edge", "text", 90, 0, 7, 4, b => b["text"] = ".")) }),
            ("481 rows tall on a 96 grid", new JsonObject { ["v"] = 1, ["grid"] = 96, ["blocks"] = new JsonArray(Block("edge", "text", 0, 0, 4, 481, b => b["text"] = ".")) }),
            ("row 500 on a grid of 24", new JsonObject { ["v"] = 1, ["blocks"] = new JsonArray(Block("edge", "text", 0, 500, 4, 4, b => b["text"] = ".")) }),
        };
        foreach (var (what, canvas) in taken) Check(ProfileItems.Valid(Item(canvas)) && ProfileItems.Canvas(Item(canvas)) is not null, "hero taken: " + what);
        foreach (var (why, canvas) in refused) Check(!ProfileItems.Valid(Item(canvas)), "hero refused: " + why);
        return new JsonObject
        {
            ["kinds"] = new JsonArray([.. ProfileItems.BlockKinds.Select(t => (JsonNode)t)]),
            ["places"] = new JsonArray([.. ProfileItems.HeroPlaces.Select(t => (JsonNode)t)]),
            ["grids"] = new JsonArray([.. ProfileItems.Grids.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Canvas)))) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["canvas"] = x.Canvas.DeepClone() })]),
        };
    }

    /// <summary>
    /// /16, a setup block's build (§2.17; ProfileItems.BuildOk): the PC's parts as the person names them, for what Exo
    /// can't read (case, power supply, cooler, fans) or a name of their own; canvases every app takes and refuses.
    /// </summary>
    private static JsonObject BuildSection(string writerId)
    {
        const long T = 1_761_700_000_000;
        JsonObject Part(string part, string name) => new() { ["part"] = part, ["name"] = name };
        JsonArray Full() => new(Part("storage", "Samsung 990 PRO"), Part("case", "NZXT H5 Flow"), Part("psu", "NZXT C850"),
            new JsonObject { ["part"] = "cooler", ["name"] = "Arctic Liquid Freezer III 240", ["later"] = "a field a newer app knows" });
        JsonObject Canvas(JsonNode build) => new()
        {
            ["v"] = 1,
            ["blocks"] = new JsonArray(new JsonObject { ["id"] = "setup", ["kind"] = "showcase", ["showcase"] = "setup", ["x"] = 0, ["y"] = 0, ["w"] = 24, ["h"] = 7, ["build"] = build }),
        };
        JsonObject Changed(int index, Action<JsonObject> change) { var build = Full(); change((JsonObject)build[index]!); return Canvas(build); }
        SyncBatch.Item Item(JsonObject canvas) => new(ProfileItems.ProfileType, ProfileItems.CanvasId, T, writerId, canvas);

        var taken = new (string What, JsonObject Canvas)[]
        {
            ("a name for a part Exo reads, three it can't, and a field a newer app knows", Canvas(Full())),
            ("no parts", Canvas(new JsonArray())),
            ("every part once", Canvas(new JsonArray([.. ProfileItems.BuildParts.Select(p => (JsonNode)Part(p, "x"))]))),
        };
        var refused = new (string Why, JsonObject Canvas)[]
        {
            ("a part twice", Canvas(new JsonArray(Part("case", "NZXT H5 Flow"), Part("case", "NZXT H9")))),
            ("a part off its list", Changed(1, p => p["part"] = "monitor")),
            ("no name", Changed(2, p => p.Remove("name"))),
            ("an empty name", Changed(2, p => p["name"] = "")),
            ("a name of 65 characters", Changed(2, p => p["name"] = new string('n', 65))),
            ("a control character in a name", Changed(1, p => p["name"] = "NZXT\tH5")),
            ("build as an object", Canvas(new JsonObject())),
        };
        foreach (var (what, canvas) in taken) Check(ProfileItems.Valid(Item(canvas)) && ProfileItems.Canvas(Item(canvas)) is not null, "build taken: " + what);
        foreach (var (why, canvas) in refused) Check(!ProfileItems.Valid(Item(canvas)), "build refused: " + why);
        return new JsonObject
        {
            ["parts"] = new JsonArray([.. ProfileItems.BuildParts.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Canvas)))) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["canvas"] = x.Canvas.DeepClone() })]),
        };
    }

    /// <summary>
    /// /17, the profile's stats (§2.17; ProfileItems.Stats): what the showcases draw, from Launcher's library, so every app
    /// and a friend's draws the same page; items every app takes and refuses, each limit at its edge.
    /// </summary>
    private static JsonObject StatsSection(string writerId)
    {
        const long T = 1_761_800_000_000;
        var sha = new string('c', 64);
        JsonObject Image(long bytes, string type = "image/webp") => new() { ["bytes"] = bytes, ["sha"] = sha, ["type"] = type };
        JsonObject Game(string id, string title, Action<JsonObject>? more = null)
        {
            var game = new JsonObject { ["id"] = id, ["title"] = title, ["minutes"] = 105_694, ["day"] = 20_391 };
            more?.Invoke(game);
            return game;
        }
        JsonObject Full() => new()
        {
            ["v"] = 1,
            ["figures"] = new JsonObject { ["minutes"] = 310_500, ["games"] = 67, ["played"] = 41, ["this-year"] = 68, ["year"] = 2026, ["installed"] = 23,
                ["stores"] = 6, ["pcs"] = 1, ["later"] = "a figure a newer app knows" },
            ["games"] = new JsonArray(
                Game("riot:valorant", "VALORANT", g => g["cover"] = Image(131_072)),
                Game("steam:252950", "Rocket League®", g => { g["art"] = new JsonObject { ["store"] = "steam", ["app"] = "252950" }; g["cover"] = Image(40_000, "image/jpeg"); }),
                Game("steam:1145360", "Hades 🔥", g => g.Remove("day")),
                Game("local:abc", new string('t', 128), g => { g.Remove("minutes"); g["later"] = "a field a newer app knows"; })),
            ["recent"] = new JsonArray("riot:valorant", "steam:252950"),
            ["most"] = new JsonArray("riot:valorant", "steam:1145360", "local:abc"),
            ["top"] = "riot:valorant",
            ["hero"] = Image(524_288),
            ["build"] = new JsonArray(new JsonObject { ["part"] = "cpu", ["name"] = "AMD 5600X" }, new JsonObject { ["part"] = "case", ["name"] = "NZXT H5 Flow" }),
            ["gear"] = new JsonArray(new JsonObject { ["kind"] = "mouse", ["maker"] = "Finalmouse", ["model"] = "Starlight X", ["picture"] = Image(90_000, "image/png") },
                new JsonObject { ["kind"] = "keyboard", ["maker"] = "Wooting", ["model"] = "60HE v2" }),
        };
        JsonObject With(Action<JsonObject> change) { var stats = Full(); change(stats); return stats; }
        JsonObject Games(int count) => new() { ["v"] = 1, ["games"] = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["id"] = "steam:" + i, ["title"] = "G" + i })]) };
        SyncBatch.Item Item(JsonObject stats) => new(ProfileItems.ProfileType, ProfileItems.StatsId, T, writerId, stats);

        var taken = new (string What, JsonObject Stats)[]
        {
            ("every field, a cover at 128 KB, the hero at 512 KB, an emoji in a title, a title of 128, and fields a newer app knows", Full()),
            ("only v", new JsonObject { ["v"] = 1 }),
            ("48 games", Games(48)),
            ("eight recent", With(s => { s["games"] = Games(8)["games"]!.DeepClone(); s["recent"] = new JsonArray([.. Enumerable.Range(0, 8).Select(i => (JsonNode)("steam:" + i))]); s["most"] = new JsonArray(); s["top"] = "steam:0"; })),
            ("figures at 0 and 10^9", With(s => { s["figures"]!["games"] = 0; s["figures"]!["minutes"] = 1_000_000_000; })),
            ("a day at 1,000,000", With(s => s["games"]![0]!["day"] = 1_000_000)),
        };
        var refused = new (string Why, JsonObject Stats)[]
        {
            ("v 2", With(s => s["v"] = 2)),
            ("49 games", Games(49)),
            ("a game twice", With(s => s["games"]!.AsArray().Add(Game("riot:valorant", "VALORANT again")))),
            ("a game without a title", With(s => s["games"]![2]!.AsObject().Remove("title"))),
            ("a title of 129", With(s => s["games"]![3]!["title"] = new string('t', 129))),
            ("a control character in a title", With(s => s["games"]![0]!["title"] = "VALO\nRANT")),
            ("an id of 129", With(s => s["games"]![3]!["id"] = new string('i', 129))),
            ("negative minutes", With(s => s["games"]![0]!["minutes"] = -1)),
            ("a day as a time", With(s => s["games"]![0]!["day"] = 1_761_800_000_000)),
            ("a day as text", With(s => s["games"]![0]!["day"] = "2025-10-30")),
            ("a cover of 128 KB and one byte", With(s => s["games"]![0]!["cover"] = Image(131_073))),
            ("a video as a cover", With(s => s["games"]![0]!["cover"] = Image(90_000, "video/mp4"))),
            ("a store with a capital", With(s => s["games"]![1]!["art"] = new JsonObject { ["store"] = "Steam", ["app"] = "252950" })),
            ("art without its app", With(s => s["games"]![1]!["art"] = new JsonObject { ["store"] = "steam" })),
            ("recent naming a game not in games", With(s => s["recent"] = new JsonArray("steam:9"))),
            ("nine most played", With(s => { s["games"] = Games(9)["games"]!.DeepClone(); s["recent"] = new JsonArray(); s["top"] = "steam:0"; s["most"] = new JsonArray([.. Enumerable.Range(0, 9).Select(i => (JsonNode)("steam:" + i))]); })),
            ("a game twice in recent", With(s => s["recent"] = new JsonArray("riot:valorant", "riot:valorant"))),
            ("top not in games", With(s => s["top"] = "steam:9")),
            ("a figure below 0", With(s => s["figures"]!["installed"] = -1)),
            ("a figure past 10^9", With(s => s["figures"]!["minutes"] = 1_000_000_001)),
            ("a figure as text", With(s => s["figures"]!["games"] = "67")),
            ("the hero at 512 KB and one byte", With(s => s["hero"] = Image(524_289))),
            ("a build part with no name", With(s => s["build"]![0]!["name"] = "")),
            ("gear of a kind off its list", With(s => s["gear"]![0]!["kind"] = "monitor")),
            ("past 32,768 bytes", With(s => s["figures"]!["later"] = new string('x', 32_768))),
        };
        foreach (var (what, stats) in taken) Check(ProfileItems.Valid(Item(stats)) && ProfileItems.Stats(Item(stats)) is not null, "stats taken: " + what);
        foreach (var (why, stats) in refused) Check(!ProfileItems.Valid(Item(stats)), "stats refused: " + why);
        return new JsonObject
        {
            ["figures"] = new JsonArray([.. ProfileItems.StatsFigures.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Stats)))) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["stats"] = x.Stats.DeepClone() })]),
        };
    }

    /// <summary>
    /// /17, what the person watched lately (§2.17; ProfileItems.Watching), for a Watching showcase in every app; items
    /// every app takes and refuses.
    /// </summary>
    private static JsonObject WatchingSection(string writerId)
    {
        const long T = 1_761_800_000_000;
        var sha = new string('d', 64);
        JsonObject Title(string id, string title, string kind) => new() { ["id"] = id, ["title"] = title, ["kind"] = kind, ["day"] = 20_391 };
        JsonObject Full() => new()
        {
            ["v"] = 1,
            ["titles"] = new JsonArray(
                Title("imdb:tt0903747", "Breaking Bad", "series"),
                new JsonObject { ["id"] = "imdb:tt15398776", ["title"] = "Oppenheimer 🎬", ["kind"] = "movie", ["poster"] = new JsonObject { ["bytes"] = 131_072, ["sha"] = sha, ["type"] = "image/webp" },
                    ["later"] = "a field a newer app knows" }),
        };
        JsonObject With(Action<JsonObject> change) { var watching = Full(); change(watching); return watching; }
        JsonObject Titles(int count) => new() { ["v"] = 1, ["titles"] = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode)Title("imdb:tt" + i, "T" + i, "movie"))]) };
        SyncBatch.Item Item(JsonObject watching) => new(ProfileItems.ProfileType, ProfileItems.WatchingId, T, writerId, watching);

        var taken = new (string What, JsonObject Watching)[]
        {
            ("a series and a movie with a poster at 128 KB, an emoji in a title, and a field a newer app knows", Full()),
            ("none", new JsonObject { ["v"] = 1, ["titles"] = new JsonArray() }),
            ("12 titles", Titles(12)),
            ("a title of 128", With(w => w["titles"]![0]!["title"] = new string('t', 128))),
        };
        var refused = new (string Why, JsonObject Watching)[]
        {
            ("no titles", new JsonObject { ["v"] = 1 }),
            ("13 titles", Titles(13)),
            ("a title twice", With(w => w["titles"]!.AsArray().Add(Title("imdb:tt0903747", "Breaking Bad", "series")))),
            ("a kind off its list", With(w => w["titles"]![0]!["kind"] = "episode")),
            ("no kind", With(w => w["titles"]![0]!.AsObject().Remove("kind"))),
            ("a title of 129", With(w => w["titles"]![0]!["title"] = new string('t', 129))),
            ("a control character in a title", With(w => w["titles"]![0]!["title"] = "Breaking\u0007Bad")),
            ("a poster of 128 KB and one byte", With(w => w["titles"]![1]!["poster"]!["bytes"] = 131_073)),
            ("a day as a time", With(w => w["titles"]![0]!["day"] = 1_761_800_000_000)),
        };
        foreach (var (what, watching) in taken) Check(ProfileItems.Valid(Item(watching)) && ProfileItems.Watching(Item(watching)) is not null, "watching taken: " + what);
        foreach (var (why, watching) in refused) Check(!ProfileItems.Valid(Item(watching)), "watching refused: " + why);
        return new JsonObject
        {
            ["kinds"] = new JsonArray([.. ProfileItems.WatchKinds.Select(t => (JsonNode)t)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = Text(CanonicalJson.Serialize(SyncBatch.Json(Item(x.Watching)))) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["watching"] = x.Watching.DeepClone() })]),
        };
    }

    /// <summary>
    /// /18, the look and canvas as one item per field (ProfileFields): what a set of items makes (the whole item the base,
    /// a field item counting only when newer than it), the items a change puts, and which field items are taken.
    /// </summary>
    private static JsonObject FieldsSection(string writerId)
    {
        const long T = 1_761_900_000_000;
        var other = new string('f', 32);
        SyncBatch.Item Whole(string id, long at, JsonObject? body) => new(ProfileItems.ProfileType, id, at, writerId, body);
        SyncBatch.Item Field(string type, string id, long at, JsonNode? body, string? device = null) => new(type, id, at, device ?? writerId, body);
        JsonObject Block(string id, int x, int at) => new() { ["id"] = id, ["kind"] = "showcase", ["showcase"] = "recent", ["x"] = x, ["y"] = 0, ["w"] = 8, ["h"] = 6, ["at"] = at };
        JsonObject Plain(JsonObject block) { var copy = (JsonObject)block.DeepClone(); copy.Remove("at"); return copy; }
        var look = new JsonObject { ["v"] = 1, ["accent"] = "#ff5fd2", ["effect"] = "rain" };
        var canvas = new JsonObject { ["v"] = 1, ["blocks"] = new JsonArray(Plain(Block("a", 0, 0)), Plain(Block("b", 8, 1))) };

        var cases = new (string What, SyncBatch.Item[] Items)[]
        {
            ("only a whole look and canvas from before /18", [Whole(ProfileItems.LookId, T, look), Whole(ProfileItems.CanvasId, T, canvas)]),
            ("a newer field changes one field; an older one under the whole look counts for nothing",
                [Whole(ProfileItems.LookId, T, look), Field(ProfileFields.LookFieldType, "accent", T + 1, JsonValue.Create("#3ee6b0")),
                 Field(ProfileFields.LookFieldType, "effect", T - 1, JsonValue.Create("snow"))]),
            ("a newer tombstone takes a field away", [Whole(ProfileItems.LookId, T, look), Field(ProfileFields.LookFieldType, "effect", T + 1, null)]),
            ("two apps' changes to two fields both stay",
                [Field(ProfileFields.LookFieldType, "accent", T + 1, JsonValue.Create("#3ee6b0")), Field(ProfileFields.LookFieldType, "frame", T + 2, JsonValue.Create("orbit"), other)]),
            ("every field taken away is no look", [Field(ProfileFields.LookFieldType, "accent", T + 1, null)]),
            ("a block moved, one added, one removed, the grid made finer; equal places go by id",
                [Whole(ProfileItems.CanvasId, T, canvas), Field(ProfileFields.CanvasBlockType, "b", T + 1, null), Field(ProfileFields.CanvasBlockType, "c", T + 2, Block("c", 4, 0)),
                 Field(ProfileFields.CanvasBlockType, "a", T + 3, Block("a", 40, 0)), Field(ProfileFields.CanvasMetaType, ProfileFields.GridId, T + 4, JsonValue.Create(96))]),
            ("a block that doesn't fit the grid is left out",
                [Field(ProfileFields.CanvasBlockType, "a", T + 1, Block("a", 40, 0)), Field(ProfileFields.CanvasBlockType, "b", T + 1, Block("b", 0, 1))]),
            ("a whole canvas saved after the fields (an app from before /18) wins over them",
                [Field(ProfileFields.CanvasBlockType, "c", T + 1, Block("c", 4, 0)), Whole(ProfileItems.CanvasId, T + 2, canvas)]),
        };
        static JsonArray Json(IEnumerable<SyncBatch.Item> items) => new([.. items.Select(item => (JsonNode)SyncBatch.Json(item))]);
        // A look or canvas of none is left out (no JSON null in the vectors, /19).
        var made = new JsonArray([.. cases.Select(c =>
        {
            var one = new JsonObject { ["what"] = c.What, ["items"] = Json(c.Items) };
            if (ProfileFields.Look(c.Items) is { } madeLook) one["look"] = madeLook.DeepClone();
            if (ProfileFields.Canvas(c.Items) is { } madeCanvas) one["canvas"] = madeCanvas.DeepClone();
            return (JsonNode)one;
        })]);
        Check(ProfileFields.Look(cases[1].Items)?["accent"]?.GetValue<string>() == "#3ee6b0" && ProfileFields.Look(cases[1].Items)?["effect"]?.GetValue<string>() == "rain", "fields: newer counts, older doesn't");
        Check(ProfileFields.Look(cases[4].Items) is null, "fields: no look");
        Check(ProfileFields.Canvas(cases[6].Items)?["blocks"]?.AsArray().Count == 1, "fields: a block off the grid is left out");
        Check(ProfileFields.Canvas(cases[7].Items)?["blocks"]?.AsArray().Count == 2, "fields: a newer whole canvas wins");

        // Changes: what's put for each, from the first case's items.
        var start = cases[0].Items;
        var changedLook = new JsonObject { ["v"] = 1, ["accent"] = "#ff5fd2", ["frame"] = "glow" };
        var changedCanvas = new JsonObject { ["v"] = 1, ["grid"] = 96, ["blocks"] = new JsonArray(Plain(Block("b", 8, 0)), Plain(Block("a", 0, 1))) };
        static JsonArray Puts(IReadOnlyList<(string Type, string Id, JsonNode? Body)> puts) => new([.. puts.Select(x =>
            (JsonNode)(x.Body is { } body ? new JsonObject { ["type"] = x.Type, ["id"] = x.Id, ["body"] = body.DeepClone() } : new JsonObject { ["type"] = x.Type, ["id"] = x.Id, ["deleted"] = true }))]);
        var puts = new JsonArray(
            new JsonObject { ["what"] = "the look: one field changed, one added, one taken away", ["look"] = changedLook.DeepClone(), ["puts"] = Puts(ProfileFields.LookPuts(start, changedLook)) },
            new JsonObject { ["what"] = "the look taken away", ["puts"] = Puts(ProfileFields.LookPuts(start, null)) },
            new JsonObject { ["what"] = "the canvas: two blocks swapped and the grid set", ["canvas"] = changedCanvas.DeepClone(), ["puts"] = Puts(ProfileFields.CanvasPuts(start, changedCanvas)) });

        var taken = new (string What, SyncBatch.Item Item)[]
        {
            ("an accent", Field(ProfileFields.LookFieldType, "accent", T, JsonValue.Create("#ff5fd2"))),
            ("a field this version doesn't know", Field(ProfileFields.LookFieldType, "later", T, JsonValue.Create("kept"))),
            ("a block at its place", Field(ProfileFields.CanvasBlockType, "a", T, Block("a", 0, 3))),
            ("the 96 grid", Field(ProfileFields.CanvasMetaType, ProfileFields.GridId, T, JsonValue.Create(96))),
            ("a tombstone", Field(ProfileFields.CanvasBlockType, "a", T, null)),
        };
        var refused = new (string Why, SyncBatch.Item Item)[]
        {
            ("an accent off its form", Field(ProfileFields.LookFieldType, "accent", T, JsonValue.Create("pink"))),
            ("the look's own v", Field(ProfileFields.LookFieldType, "v", T, JsonValue.Create(1))),
            ("a block whose id isn't its item's", Field(ProfileFields.CanvasBlockType, "b", T, Block("a", 0, 0))),
            ("a block with no place", Field(ProfileFields.CanvasBlockType, "a", T, Plain(Block("a", 0, 0)))),
            ("a block at place 48", Field(ProfileFields.CanvasBlockType, "a", T, Block("a", 0, 48))),
            ("a grid of 48", Field(ProfileFields.CanvasMetaType, ProfileFields.GridId, T, JsonValue.Create(48))),
            ("a canvas field this version doesn't know", Field(ProfileFields.CanvasMetaType, "zoom", T, JsonValue.Create(2))),
        };
        foreach (var (what, item) in taken) Check(ProfileItems.Valid(item), "field taken: " + what);
        foreach (var (why, item) in refused) Check(!ProfileItems.Valid(item), "field refused: " + why);
        return new JsonObject
        {
            ["made"] = made,
            ["puts"] = puts,
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = SyncBatch.Json(x.Item) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["item"] = SyncBatch.Json(x.Item) })]),
        };
    }

    /// <summary>/18, who sees a block (a canvas block's privacy) and a game or title hidden from other people.</summary>
    private static JsonObject PrivacySection(string writerId)
    {
        const long T = 1_761_900_000_000;
        JsonObject Canvas(string? privacy)
        {
            var block = new JsonObject { ["id"] = "a", ["kind"] = "showcase", ["showcase"] = "recent", ["x"] = 0, ["y"] = 0, ["w"] = 8, ["h"] = 6 };
            if (privacy is not null) block["privacy"] = privacy;
            return new JsonObject { ["v"] = 1, ["blocks"] = new JsonArray(block) };
        }
        SyncBatch.Item CanvasItem(string? privacy) => new(ProfileItems.ProfileType, ProfileItems.CanvasId, T, writerId, Canvas(privacy));
        SyncBatch.Item Hidden(string id, JsonNode? body) => new(ProfileItems.HiddenGameType, id, T, writerId, body);
        foreach (var privacy in ProfileItems.Privacies) Check(ProfileItems.Valid(CanvasItem(privacy)), "privacy taken: " + privacy);
        Check(!ProfileItems.Valid(CanvasItem("public")), "privacy refused: off its list");
        var taken = new (string What, SyncBatch.Item Item)[]
        {
            ("a Steam game", Hidden("steam:570", JsonValue.Create(true))),
            ("a title", Hidden("imdb:tt0903747", JsonValue.Create(true))),
            ("shown again", Hidden("steam:570", null)),
        };
        var refused = new (string Why, SyncBatch.Item Item)[]
        {
            ("false as its body", Hidden("steam:570", JsonValue.Create(false))),
            ("an id of 129", Hidden(new string('g', 129), JsonValue.Create(true))),
            ("a control character in its id", Hidden("steam:\u0007", JsonValue.Create(true))),
        };
        foreach (var (what, item) in taken) Check(ProfileItems.Valid(item), "hidden taken: " + what);
        foreach (var (why, item) in refused) Check(!ProfileItems.Valid(item), "hidden refused: " + why);
        return new JsonObject
        {
            ["privacies"] = new JsonArray([.. ProfileItems.Privacies.Select(x => (JsonNode)x)]),
            ["taken"] = new JsonArray([.. taken.Select(x => (JsonNode)new JsonObject { ["what"] = x.What, ["item"] = SyncBatch.Json(x.Item) })]),
            ["refused"] = new JsonArray([.. refused.Select(x => (JsonNode)new JsonObject { ["why"] = x.Why, ["item"] = SyncBatch.Json(x.Item) })]),
        };
    }

    /// <summary>
    /// /18, what a PC carries across leaving a profile (account plan item 2) and how it comes back: back in the same profile
    /// each carried item merges by the sync rule (SyncedItems.Merge), keeping its own time and writer; in another, only the
    /// profile's items fill what it lacks, stamped 0. Every case: what was held after the catch-up, what was carried, and
    /// what is held after, with the items that go out again.
    /// </summary>
    private static JsonArray CarrySection(string writerId)
    {
        const long T = 1_761_900_000_000;
        var higher = new string('f', 32);
        SyncBatch.Item Look(long at, string device, string accent) => new(ProfileFields.LookFieldType, "accent", at, device, JsonValue.Create(accent));
        (string What, bool Same, SyncBatch.Item[] Held, SyncBatch.Item[] Carried)[] cases =
        [
            ("same profile: a newer carried change wins and goes again", true, [Look(T, higher, "#111111")], [Look(T + 1, writerId, "#222222")]),
            ("same profile: an older carried change loses", true, [Look(T + 1, writerId, "#111111")], [Look(T, higher, "#222222")]),
            ("same profile: a tie in time goes to the higher device", true, [Look(T, writerId, "#111111")], [Look(T, higher, "#222222")]),
            ("same profile: what the box lost comes back", true, [], [Look(T, writerId, "#222222")]),
            ("another profile: only what it lacks, stamped 0", false, [Look(T, higher, "#111111")],
                [Look(T + 5, writerId, "#222222"), new(ProfileItems.ProfileType, ProfileItems.NameId, T + 5, writerId, JsonValue.Create("Erix"))]),
        ];
        static JsonArray Json(IEnumerable<SyncBatch.Item> items) => new([.. items.OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).Select(item => (JsonNode)SyncBatch.Json(item))]);
        return new JsonArray([.. cases.Select(c =>
        {
            var holder = new SyncedItems(ProfileItems.App);
            holder.Take(c.Held);
            foreach (var item in c.Carried)
                if (c.Same) holder.Merge(item);
                else if (item.Body is { } body) holder.Seed(item.Type, item.Id, body);
            return (JsonNode)new JsonObject { ["what"] = c.What, ["sameProfile"] = c.Same, ["held"] = Json(c.Held), ["carried"] = Json(c.Carried), ["after"] = Json(holder.Items), ["goesOut"] = Json(holder.Unsent) };
        })]);
    }

    /// <summary>
    /// /19, the profile's media (MediaSync, MediaStore; account plan item 6), from the vectors' sync key: the media box's
    /// sub-key and id; a stored part's name for a SHA-256 and part, and its sealed body from a fixed nonce, with its
    /// associated data; the bytes a PUT's signature is over at a fixed time; and box rows, one carrying a part's bytes and
    /// one a stored file's pointer.
    /// </summary>
    private static JsonObject MediaSection(byte[] syncKey)
    {
        var subKey = Envelope.SyncSubKey(syncKey, MediaSync.App);
        var boxPublic = SyncSession.BoxPublicKey(subKey);
        var boxId = Convert.FromHexString(Mailbox.BoxId(boxPublic));
        var plain = Encoding.UTF8.GetBytes("a part of a profile's video, as it is on the PC");
        var sha = H(SHA256.HashData("a video"u8));
        var nonce = Convert.FromHexString("000102030405060708090a0b");
        var names = new[] { 0, 1 }.Select(part => MediaSync.PartName(subKey, sha, part)).ToArray();
        var sealedPart = MediaSync.Seal(subKey, boxId, names[0], plain, nonce);
        Check(MediaSync.Open(subKey, boxId, names[0], sealedPart) is { } opened && opened.AsSpan().SequenceEqual(plain), "media: a sealed part opens");
        Check(MediaSync.Open(subKey, boxId, names[1], sealedPart) is null, "media: a part under another name doesn't open");
        const string Time = "1761900000";
        var small = new MediaSync.Wanted(H(SHA256.HashData("a picture"u8)), "image/webp", 150_000);
        var big = new MediaSync.Wanted(sha, "video/mp4", 40_000_000);
        return new JsonObject
        {
            ["subKey"] = H(subKey),
            ["boxId"] = H(boxId),
            ["sha"] = sha,
            ["names"] = new JsonArray([.. names.Select((name, part) => (JsonNode)new JsonObject { ["part"] = part, ["name"] = name })]),
            ["seal"] = new JsonObject
            {
                ["name"] = names[0],
                ["nonce"] = H(nonce),
                ["plain"] = H(plain),
                ["aad"] = H(MediaSync.Bound(boxId, names[0])),
                ["sealed"] = H(sealedPart),
            },
            ["putSigned"] = new JsonObject { ["method"] = "PUT", ["name"] = names[0], ["time"] = Time, ["body"] = H(sealedPart), ["signed"] = H(MediaStore.Signed("PUT", boxId, names[0], Time, sealedPart)) },
            ["getSigned"] = new JsonObject { ["method"] = "GET", ["name"] = names[0], ["time"] = Time, ["signed"] = H(MediaStore.Signed("GET", boxId, names[0], Time, [])) },
            ["rows"] = new JsonArray(
                new JsonObject { ["what"] = "the second of three parts of a picture in rows", ["row"] = H(MediaSync.Row(small, 1, 3, plain)) },
                new JsonObject { ["what"] = "a stored video's pointer: two parts in the store, no bytes", ["row"] = H(MediaSync.Row(big, 0, 2, [], stored: true)) }),
        };
    }

    /// <summary>A change's canonical form without its own sig (an add's nested record keeps its own): what's hashed.</summary>
    private static byte[] Hashed(JsonObject change)
    {
        var unsigned = (JsonObject)change.DeepClone();
        unsigned.Remove("sig");
        var bytes = CanonicalJson.Serialize(unsigned);
        Check(H(SHA256.HashData(bytes)) == CanonicalJson.Hash(change), "a change's hash leaves out its own sig only");
        return bytes;
    }

    private static bool SameAction(JsonObject x, JsonObject y) =>
        Device.Text(x, "by") == Device.Text(y, "by") && Device.Text(x, "op") == Device.Text(y, "op") && Device.Int(x, "at") == Device.Int(y, "at") &&
        CanonicalJson.Serialize(x["device"]!).AsSpan().SequenceEqual(CanonicalJson.Serialize(y["device"]!));

    /// <summary>
    /// Plan §2.13's fork rule. A base of five (A, then B, C and D added by A in that order, then the recovery entry R),
    /// and after its head, changes made at once: one case per rule, a three-way cycle, a four-way fork, the recovery code
    /// in a race against a stolen PC, a losing branch of three changes, two losers by different authors signed again in
    /// rank order, and an equivocation. Each winner comes from DeviceList.Winner and is checked by taking the changes in
    /// every order (a branch's later changes too, sent again when they came before their predecessor): every order must
    /// end on the same head, and every order of everything (the re-signs and later changes too) on the same final head.
    /// Where a priority rule decides, the winner doesn't have the lowest hash, so a hash-only checker fails it. Also
    /// changes a list takes or refuses outright on that base, each signed by a device in it.
    /// </summary>
    private static (JsonObject Forks, JsonArray Refused, JsonArray Accepted) Forks(Signatures signed, VectorSigner a, JsonObject aRecord, VectorSigner b, JsonObject bRecord,
        VectorSigner r, JsonObject rRecord, JsonObject secondEntry)
    {
        using var c = new VectorSigner(P256.Signer(Scalar("tower")), signed);
        using var d = new VectorSigner(P256.Signer(Scalar("media pc")), signed);
        using var e = new VectorSigner(P256.Signer(Scalar("den pc")), signed);
        using var cEnc = P256.Agreement(Scalar("tower encryption"));
        using var dEnc = P256.Agreement(Scalar("media pc encryption"));
        using var eEnc = P256.Agreement(Scalar("den pc encryption"));
        var start = DeviceList.Start(a, aRecord, 1_761_000_000);
        start.Add(a, bRecord, 1_761_000_100);
        start.Add(a, Device.Create(c, P256.PublicKey(cEnc), "Tower", ["launcher"], 1_761_000_110), 1_761_000_110);
        start.Add(a, Device.Create(d, P256.PublicKey(dEnc), "Media PC", ["cinema"], 1_761_000_120), 1_761_000_120);
        start.Add(a, rRecord, 1_761_000_200);
        DeviceList Copy() => DeviceList.Load(start.Changes)!;
        static string Id(IDeviceSigner key) => Device.IdOf(key.PublicKey);
        var authors = new[] { a, b, c, d, r }.ToDictionary(key => Id(key));
        var (aId, bId, cId) = (Id(a), Id(b), Id(c));
        static bool Below(string hash, string than) => string.CompareOrdinal(hash, than) < 0;
        const long T = 1_761_000_600;

        // A change made on a fresh copy, its time moved until its hash fits; searched with the same key outside the
        // vectors (the hash doesn't cover the signature), then signed once for the file.
        JsonObject Find(VectorSigner by, Func<DeviceList, IDeviceSigner, long, JsonObject> make, long from, Func<string, bool> fits)
        {
            for (var at = from; ; at++)
                if (fits(CanonicalJson.Hash(make(Copy(), by.Unrecorded, at)))) return make(Copy(), by, at);
        }

        // A device taking the changes in this order, each change sent again while its predecessor hasn't come.
        DeviceList Land(IEnumerable<JsonObject> order, string rule)
        {
            var list = Copy();
            var pending = new Queue<JsonObject>(order);
            for (var tries = 0; pending.Count > 0; tries++)
            {
                Check(tries < 100, "every change lands");
                var took = list.Take(pending.Peek()).Result;
                Check(took != DeviceList.Result.Invalid, "no change is refused: " + rule);
                if (took == DeviceList.Result.Gap) pending.Enqueue(pending.Dequeue());
                else pending.Dequeue();
            }
            return list;
        }

        var cases = new JsonArray();
        void Case(string rule, JsonObject[] changes, int expected, bool priority, JsonObject[]? branch = null, Dictionary<string, string>? dropped = null,
            JsonObject[]? onTop = null, JsonObject[][]? arrivals = null)
        {
            foreach (var change in changes) Check(Copy().Take(change).Result == DeviceList.Result.Appended, "each change applies to the base: " + rule);
            var hashes = changes.Select(CanonicalJson.Hash).ToArray();
            var winner = CanonicalJson.Hash(Copy().Winner(changes)!);
            Check(winner == hashes[expected], rule);
            var lowest = Array.IndexOf(hashes, hashes.Min(StringComparer.Ordinal));
            Check(priority == (lowest != expected), "a hash-only rule picks another one exactly where a priority rule decides: " + rule);
            foreach (var order in Orders([.. changes, .. branch ?? []]))
                Check(Land(order, rule).Head == winner, "every arrival order ends on the same head: " + rule);
            var result = new JsonObject
            {
                ["rule"] = rule,
                ["changes"] = new JsonArray([.. changes.Select(change => change.DeepClone())]),
                ["hashes"] = new JsonArray([.. hashes.Select(hash => (JsonNode)hash)]),
                ["winner"] = expected,
                ["head"] = winner,
            };
            if (branch is not null) result["branch"] = new JsonArray([.. branch.Select(change => change.DeepClone())]);
            if (onTop is not null) result["after"] = new JsonArray([.. onTop.Select(change => change.DeepClone())]);
            // A device that has seen all of it: the fork, the branch behind a loser, and what came on top of the winner.
            var after = Copy();
            JsonObject[] seen = [.. changes, .. branch ?? [], .. onTop ?? []];
            foreach (var change in seen) Check(after.Take(change).Result != DeviceList.Result.Invalid, "the changes land: " + rule);
            var resigned = new List<JsonObject>();
            if (dropped is not null)
            {
                // The losers are signed again in their authors' rank order, one after another: each author signs its own
                // when its turn comes (Resign stops at another author's that still applies), until no one has more.
                JsonObject[] lost = [.. changes.Where((_, i) => i != expected), .. branch ?? []];
                for (var progress = true; progress;)
                {
                    progress = false;
                    foreach (var author in lost.Select(change => Device.Text(change, "by")!).Distinct().Order(StringComparer.Ordinal))
                    {
                        var made = after.Resign(lost, authors[author]);
                        resigned.AddRange(made);
                        progress |= made.Count > 0;
                    }
                }
                var gone = lost.Where(change => !resigned.Any(x => SameAction(x, change))).Select(CanonicalJson.Hash).Order(StringComparer.Ordinal);
                Check(gone.SequenceEqual(dropped.Keys.Order(StringComparer.Ordinal)), "what's dropped on re-signing: " + rule);
                Check(resigned.All(x => lost.Any(y => SameAction(x, y))), "a re-signed change keeps its by, op, device and at");
                var resignedRanks = resigned.Select(x => Copy().Rank(Device.Text(x, "by")!)).ToList();
                Check(resignedRanks.SequenceEqual(resignedRanks.Order()), "re-signed in rank order: " + rule);
                result["resigned"] = new JsonArray([.. resigned.Select(change => change.DeepClone())]);
                result["droppedOnResign"] = new JsonArray([.. dropped.Select(p => (JsonNode)new JsonObject { ["hash"] = p.Key, ["why"] = p.Value })]);
            }
            // Everything, in every order: one head.
            foreach (var order in Orders([.. changes, .. branch ?? [], .. onTop ?? [], .. resigned]))
                Check(Land(order, rule).Head == after.Head, "every arrival order of everything ends on the same head: " + rule);
            result["finalHead"] = after.Head;
            if (after.Equivocators is { Count: > 0 } marked) result["equivocated"] = new JsonArray([.. marked.Select(id => (JsonNode)id)]);
            if (arrivals is not null)
            {
                // Orders spelled out: the head after each arrival.
                result["arrivals"] = new JsonArray([.. arrivals.Select(order =>
                {
                    var list = Copy();
                    var heads = order.Select(change =>
                    {
                        Check(list.Take(change).Result != DeviceList.Result.Invalid, "an arrival lands: " + rule);
                        return (JsonNode)list.Head;
                    }).ToList();
                    Check(list.Head == after.Head, "a spelled-out order ends on the final head: " + rule);
                    return (JsonNode)new JsonObject
                    {
                        ["order"] = new JsonArray([.. order.Select(change => (JsonNode)CanonicalJson.Hash(change))]),
                        ["heads"] = new JsonArray([.. heads]),
                    };
                })]);
            }
            result["devicesAfter"] = new JsonArray([.. after.Devices.Select(x => x.Id).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]);
            cases.Add(result);
        }

        var removeB = Copy().Remove(a, bId, T);
        var bRenames = Find(b, (l, k, at) => l.Rename(k, bId, "Travel laptop", at), T, h => Below(h, CanonicalJson.Hash(removeB)));
        Case("1. a remove beats any concurrent change by the device it removes", [removeB, bRenames], 0, priority: true,
            dropped: new() { [CanonicalJson.Hash(bRenames)] = "its author was removed" });

        var bRemovesA = Find(b, (l, k, at) => l.Remove(k, aId, at), T, h => Below(h, CanonicalJson.Hash(removeB)));
        Case("2. two devices remove each other: the one added earlier wins, so a stolen PC added first beats a newer one (the recovery code takes it back: case 6)",
            [removeB, bRemovesA], 0, priority: true, dropped: new() { [CanonicalJson.Hash(bRemovesA)] = "its author was removed" });

        var aRenames = Copy().Rename(a, aId, "Desk", T + 100);
        var bRenamesToo = Copy().Rename(b, bId, "Laptop", T + 100);
        var lower = Below(CanonicalJson.Hash(aRenames), CanonicalJson.Hash(bRenamesToo)) ? 0 : 1;
        Case("3. otherwise the lower hash wins; the loser's author signs it again on top with the next seq and the new head as prev, keeping its at",
            [aRenames, bRenamesToo], lower, priority: false, dropped: new());

        // A cycle. Pairwise, A's remove beats B's rename, B's beats C's by hash and C's beats A's by hash.
        var cRenames = Find(c, (l, k, at) => l.Rename(k, cId, "Tower 2", at), T + 200, h => Below(h, CanonicalJson.Hash(removeB)));
        var bRenamesLow = Find(b, (l, k, at) => l.Rename(k, bId, "Laptop 2", at), T + 200, h => Below(h, CanonicalJson.Hash(cRenames)));
        Case("4. a three-way cycle (pairwise: A's remove beats B's rename, B's beats C's by hash, C's beats A's by hash): B is struck, then the lower hash of what's left; the losers are signed again in rank order, so A's remove of B goes first and B's rename is dropped",
            [removeB, bRenamesLow, cRenames], 2, priority: true, dropped: new() { [CanonicalJson.Hash(bRenamesLow)] = "its author was removed" });

        var dRemovesC = Find(d, (l, k, at) => l.Remove(k, cId, at), T + 300, h => Below(h, CanonicalJson.Hash(removeB)));
        var bRemovesA4 = Find(b, (l, k, at) => l.Remove(k, aId, at), T + 300, h => Below(h, CanonicalJson.Hash(dRemovesC)));
        var cRenames4 = Find(c, (l, k, at) => l.Rename(k, cId, "Tower 3", at), T + 300, h => Below(h, CanonicalJson.Hash(bRemovesA4)));
        Case("5. four at once (B and A remove each other, C renames, D removes C): A strikes B, D strikes C, then the lower hash of A's and D's; in rank order A's remove of B is signed again first, so B's remove of A (lower hash) is dropped",
            [bRemovesA4, removeB, cRenames4, dRemovesC], 3, priority: true,
            dropped: new() { [CanonicalJson.Hash(bRemovesA4)] = "its author was removed", [CanonicalJson.Hash(cRenames4)] = "its author was removed" });

        var rRemovesA = Find(r, (l, k, at) => l.Remove(k, aId, at), T + 400, h => Below(h, CanonicalJson.Hash(bRemovesA)));
        var aRemovesBLow = Find(a, (l, k, at) => l.Remove(k, bId, at), T + 400, h => Below(h, CanonicalJson.Hash(rRemovesA)));
        Case("6. the recovery code in the race of case 2: it ranks before every PC, so its remove strikes A (the stolen PC added first) and the lower hash of R's and B's wins",
            [aRemovesBLow, bRemovesA, rRemovesA], 2, priority: true,
            dropped: new() { [CanonicalJson.Hash(aRemovesBLow)] = "its author was removed", [CanonicalJson.Hash(bRemovesA)] = "the device it removes is gone" });

        var removeC = Copy().Remove(a, cId, T + 500);
        var renameC = Find(b, (l, k, at) => l.Rename(k, cId, "Old tower", at), T + 500, h => !Below(h, CanonicalJson.Hash(removeC)));
        var onB = Copy();
        onB.Take(renameC);
        var renameB = onB.Rename(b, bId, "Travel laptop", T + 510);
        var addE = onB.Add(b, Device.Create(e, P256.PublicKey(eEnc), "Den PC", ["launcher"], T + 520), T + 520);
        Case("7. a losing branch of three is signed again in order by its author, each change keeping its content and at; one that no longer applies (it renames C, which the winner removed) is dropped and the rest go on",
            [removeC, renameC], 0, priority: false, branch: [renameB, addE], dropped: new() { [CanonicalJson.Hash(renameC)] = "the device it renames is gone" });

        // Two losers that both still apply, by B (rank 1) and C (rank 2). C's has the lower hash and stands first here, so
        // only the ranks give the order: B's on top of the head, then C's on top of B's.
        var bRenames8 = Copy().Rename(b, bId, "Laptop 8", T + 800);
        var cRenames8 = Find(c, (l, k, at) => l.Rename(k, cId, "Tower 8", at), T + 800, h => Below(h, CanonicalJson.Hash(bRenames8)));
        var dRenames8 = Find(d, (l, k, at) => l.Rename(k, Id(d), "Media room", at), T + 800, h => Below(h, CanonicalJson.Hash(cRenames8)));
        Case("8. two losers that still apply, by different authors: each is signed again on top of the new head in its author's rank order, one after another (B's, then C's on top of it), whatever their hashes or arrival order",
            [cRenames8, dRenames8, bRenames8], 1, priority: false, dropped: new());

        // C signs two different changes after the base head; both have lower hashes than D's change at the same time, and
        // one removes D, which would strike D's change if it counted. Both are out in any order; D's wins, and C's next
        // change, on top of it, counts again.
        var dRenames9 = Copy().Rename(d, Id(d), "Media PC 9", T + 900);
        var cRenames9 = Find(c, (l, k, at) => l.Rename(k, cId, "Tower 9", at), T + 900, h => Below(h, CanonicalJson.Hash(dRenames9)));
        var cRemovesD9 = Find(c, (l, k, at) => l.Remove(k, Id(d), at), T + 900, h => Below(h, CanonicalJson.Hash(dRenames9)));
        var onD9 = Copy();
        onD9.Take(dRenames9);
        var cHonest9 = onD9.Rename(c, cId, "Tower", T + 910);
        Case("9. equivocation: C signs two different changes after the same head; whoever sees both drops both (in either order) and marks C, which stays listed; D's change wins, and C's next change at a higher seq counts again",
            [cRenames9, cRemovesD9, dRenames9], 2, priority: true, onTop: [cHonest9],
            dropped: new() { [CanonicalJson.Hash(cRenames9)] = "an equivocation: never signed again", [CanonicalJson.Hash(cRemovesD9)] = "an equivocation: never signed again" },
            arrivals: [[cRenames9, cRemovesD9, dRenames9, cHonest9], [cRemovesD9, cRenames9, dRenames9, cHonest9]]);

        // Signed by a device in the list, and refused anyway.
        var refused = new JsonArray();
        void Refuse(VectorSigner by, string op, JsonNode device, string why, string? extra = null)
        {
            var change = new JsonObject
            {
                ["v"] = 1, ["exoId"] = start.ExoId, ["seq"] = start.Changes.Count, ["prev"] = start.Head, ["op"] = op, ["device"] = device, ["by"] = Id(by), ["at"] = T + 700,
            };
            if (extra is not null) change[extra] = "x";
            change = CanonicalJson.Sign(change, DeviceList.ChangeLabel, by);
            Check(Copy().Take(change).Result == DeviceList.Result.Invalid, "refused: " + why);
            refused.Add(new JsonObject { ["change"] = change, ["why"] = why });
        }
        Refuse(a, "remove", Id(r), "a PC removes the recovery entry (only the code turns recovery off)");
        Refuse(b, "add", secondEntry.DeepClone(), "a PC adds a second recovery entry (only the code replaces its own)");
        Refuse(r, "rename", new JsonObject { ["id"] = bId, ["name"] = "Mine now" }, "the recovery entry renames a device");
        Refuse(r, "rename", new JsonObject { ["id"] = Id(r), ["name"] = "My code" }, "the recovery entry renames itself");
        Refuse(a, "rename", new JsonObject { ["id"] = Id(r), ["name"] = "Old code" }, "a PC renames the recovery entry (no one renames it)");
        Refuse(b, "rename", new JsonObject { ["id"] = bId, ["name"] = "Lap" + Cp(0x200B) + "top" }, "a name with a zero-width space");
        Refuse(a, "rename", new JsonObject { ["id"] = bId, ["name"] = "Laptop" }, "a field a change doesn't have", extra: "note");
        // A PC's record, signed by its own key, with the flag written as false: refused, the flag is there only as true.
        var notRecovery = CanonicalJson.Sign(new JsonObject
        {
            ["v"] = 1, ["id"] = Id(e), ["name"] = "Den PC", ["created"] = T + 700, ["sigPub"] = System.Buffers.Text.Base64Url.EncodeToString(e.PublicKey),
            ["encPub"] = System.Buffers.Text.Base64Url.EncodeToString(P256.PublicKey(eEnc)), ["apps"] = new JsonArray("launcher"), ["recovery"] = false,
        }, Device.Label, e);
        Check(Device.Read(notRecovery.DeepClone()) is null, "a record with recovery false is refused");
        Refuse(a, "add", notRecovery, "a device record with \"recovery\": false (the flag is there only as true)");

        // Signed by a device in the list and taken: the two removals of oneself.
        var accepted = new JsonArray();
        void Accept(VectorSigner by, string op, JsonNode device, string why)
        {
            var change = CanonicalJson.Sign(new JsonObject
            {
                ["v"] = 1, ["exoId"] = start.ExoId, ["seq"] = start.Changes.Count, ["prev"] = start.Head, ["op"] = op, ["device"] = device, ["by"] = Id(by), ["at"] = T + 700,
            }, DeviceList.ChangeLabel, by);
            var list = Copy();
            Check(list.Take(change).Result == DeviceList.Result.Appended, "taken: " + why);
            accepted.Add(new JsonObject
            {
                ["change"] = change,
                ["why"] = why,
                ["devicesAfter"] = new JsonArray([.. list.Devices.Select(x => x.Id).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
            });
        }
        Accept(r, "remove", Id(r), "the recovery entry removes itself: recovery off (only the code may)");
        Accept(b, "remove", bId, "a PC removes itself: it leaves the identity");

        var ranks = new JsonObject();
        foreach (var (id, rank) in new[] { (Id(r), -1), (aId, 0), (bId, 1), (cId, 2), (Id(d), 3) }) ranks[id] = rank;
        return (new JsonObject
        {
            ["base"] = new JsonArray([.. start.Changes.Select(change => change.DeepClone())]),
            ["ranks"] = ranks,
            ["cases"] = cases,
        }, refused, accepted);
    }

    private static string Cp(params int[] scalars) => string.Concat(scalars.Select(char.ConvertFromUtf32));

    private static (int From, int To)[] Ranges(string[] ranges) =>
        [.. ranges.Select(range => range.Split('-')).Select(ends => (Convert.ToInt32(ends[0], 16), Convert.ToInt32(ends[^1], 16)))];

    private static bool In((int From, int To)[] ranges, int c) => ranges.Any(range => c >= range.From && c <= range.To);

    /// <summary>A scalar from key material, with every candidate HKDF-Expand gave before the one in range.</summary>
    private static JsonObject Derived(byte[] prk, string info, string? note)
    {
        var label = Encoding.ASCII.GetBytes(info);
        var rejected = new JsonArray();
        var candidate = new byte[32];
        var counter = 0;
        for (; ; counter++)
        {
            HKDF.Expand(HashAlgorithmName.SHA256, prk, candidate, [.. label, (byte)counter]);
            if (P256.InRange(candidate)) break;
            rejected.Add(H(candidate));
        }
        var scalar = P256.DeriveScalar(prk, label);
        Check(scalar.SequenceEqual(candidate), "DeriveScalar takes the first candidate in range");
        Check((note is null) == (counter == 0), "only the retry case needs a second candidate");
        using var key = P256.Signer(scalar);
        var result = new JsonObject { ["prk"] = H(prk), ["info"] = info, ["counter"] = counter, ["rejected"] = rejected, ["scalar"] = H(scalar), ["publicKey"] = H(P256.PublicKey(key)) };
        if (note is not null) result["note"] = note;
        return result;
    }

    private static JsonObject Box(string from, byte[] key)
    {
        var scalar = Mailbox.BoxScalar(key);
        using var signer = P256.Signer(scalar);
        var publicKey = P256.PublicKey(signer);
        return new JsonObject { ["from"] = from, ["scalar"] = H(scalar), ["publicKey"] = H(publicKey), ["boxId"] = Mailbox.BoxId(publicKey) };
    }

    /// <summary>Signatures made for the vectors: one made earlier in this build, or already in the file, is used again
    /// while it verifies over the same message, so the file only changes when what's signed does.</summary>
    private sealed class Signatures(JsonArray? previous)
    {
        internal JsonArray Used { get; } = [];

        internal byte[] Sign(IDeviceSigner key, ReadOnlySpan<byte> message)
        {
            var publicKey = H(key.PublicKey);
            var text = H(message);
            var bytes = message.ToArray();
            byte[]? Find(JsonArray? list) => list?.OfType<JsonObject>()
                .Where(e => e["publicKey"]?.GetValue<string>() == publicKey && e["message"]?.GetValue<string>() == text)
                .Select(e => Convert.FromHexString(e["signature"]!.GetValue<string>()))
                .FirstOrDefault(s => P256.Verify(key.PublicKey, bytes, s));
            if (Find(Used) is { } again) return again;
            var signature = Find(previous) ?? (key is VectorSigner v ? v.Fresh(message) : key.Sign(message));
            Used.Add(new JsonObject { ["publicKey"] = publicKey, ["message"] = text, ["signature"] = H(signature) });
            return signature;
        }
    }

    private sealed class VectorSigner(ECDsa key, Signatures signatures) : IDeviceSigner, IDisposable
    {
        private readonly EcdsaSigner _inner = new(key);

        public byte[] PublicKey => _inner.PublicKey;

        /// <summary>The same key, its signatures kept out of the file (for searching).</summary>
        internal IDeviceSigner Unrecorded => _inner;

        public byte[] Sign(ReadOnlySpan<byte> data) => signatures.Sign(this, data);

        internal byte[] Fresh(ReadOnlySpan<byte> data) => _inner.Sign(data);

        public void Dispose() => _inner.Dispose();
    }
}
