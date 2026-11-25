using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class ExoPolicyShadowParityTests
{
    private static JsonDocument LoadGolden()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "native-ui",
                "crates",
                "exo-policy",
                "tests",
                "fixtures",
                "exo-policy-golden.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate));
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate native-ui/crates/exo-policy/tests/fixtures/exo-policy-golden.json");
    }

    [Fact]
    public void GoldenProcessTerminationVectorsMatchCSharp()
    {
        using var doc = LoadGolden();
        foreach (var row in doc.RootElement.GetProperty("processTermination").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString();
            var expected = row.GetProperty("isAntiCheat").GetBoolean();
            Assert.Equal(expected, ProcessTermination.IsAntiCheat(name));
        }

        foreach (var tree in doc.RootElement.GetProperty("processTrees").EnumerateArray())
        {
            var r = tree.GetProperty("root");
            var root = (r.GetProperty("pid").GetInt32(), r.GetProperty("name").GetString());
            var desc = tree.GetProperty("descendants")
                .EnumerateArray()
                .Select(d => (d.GetProperty("pid").GetInt32(), d.GetProperty("name").GetString()))
                .ToArray();
            var (kill, refused) = ProcessTermination.PlanTree(root, desc);
            var expectedKill = tree.GetProperty("expectedKill").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var expectedRefused = tree.GetProperty("expectedRefused").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(expectedKill, kill);
            Assert.Equal(expectedRefused, refused);
        }
    }

    [Fact]
    public void GoldenNetworkAndPlayerDataVectorsMatchCSharp()
    {
        using var doc = LoadGolden();
        foreach (var row in doc.RootElement.GetProperty("networkHosts").EnumerateArray())
        {
            var host = row.GetProperty("host").GetString()!;
            var expected = row.GetProperty("listed").GetBoolean();
            Assert.Equal(expected, NetworkDestinations.IsListed(host));
        }

        foreach (var row in doc.RootElement.GetProperty("playerData").EnumerateArray())
        {
            var path = row.GetProperty("path").GetString()!;
            var expectedMatched = row.GetProperty("matched").GetBoolean();
            var expectedLocal = row.GetProperty("ofThisMachine").GetBoolean();
            var actual = PlayerData.Of(path);
            Assert.Equal(expectedMatched, actual is not null);
            if (actual is not null)
                Assert.Equal(expectedLocal, actual.OfThisMachine);
        }

        foreach (var row in doc.RootElement.GetProperty("uiPrefs").EnumerateArray())
        {
            var key = row.GetProperty("key").GetString()!;
            var expected = row.GetProperty("syncs").GetBoolean();
            Assert.Equal(expected, PlayerData.UiPrefSyncs(key));
        }
    }

    [Fact]
    public void GoldenDeletionGateAndAntiCheatVectorsMatchCSharp()
    {
        using var doc = LoadGolden();
        var policy = new DeletionGate.Policy(
            KeptRoots: [@"C:\Users\Test\Documents\Exo\Backups"],
            ExoData: @"C:\Users\Test\AppData\Local\ExoLauncher",
            ShaderRoots: [@"C:\Users\Test\AppData\Local\NVIDIA"],
            Running: () => ["steam", "discord"],
            ShaderBusy: () => false);

        foreach (var row in doc.RootElement.GetProperty("deletionGate").EnumerateArray())
        {
            var path = row.GetProperty("path").GetString()!;
            var clean = row.GetProperty("clean").GetBoolean();
            var owners = row.GetProperty("owners").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var allowBrowser = row.GetProperty("allowBrowser").GetBoolean();
            var expectedAllowed = row.GetProperty("allowed").GetBoolean();
            var refusal = DeletionGate.Refusal(path, clean, owners, policy, allowBrowser);
            Assert.Equal(expectedAllowed, refusal is null);
        }

        foreach (var row in doc.RootElement.GetProperty("antiCheat").EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            var title = row.GetProperty("title").GetString()!;
            var store = row.GetProperty("store").GetString()! switch
            {
                "riot" => StoreKind.Riot,
                "steam" => StoreKind.Steam,
                _ => StoreKind.Local,
            };
            var installFound = row.GetProperty("installFound").GetBoolean();
            var disk = row.GetProperty("disk").ValueKind == JsonValueKind.Null ? null : row.GetProperty("disk").GetString();
            var steam = row.GetProperty("steam").GetString()! switch
            {
                "none" => AntiCheatCheck.SteamWord.None,
                "vac" => AntiCheatCheck.SteamWord.Vac,
                _ => AntiCheatCheck.SteamWord.NotSteam,
            };

            var game = new GameEntry { Id = id, Title = title, Store = store };
            var answer = AntiCheatCheck.Decide(
                AntiCheatCheck.ForStore(store),
                AntiCheatCheck.KnownGames.GetValueOrDefault(id),
                installFound,
                disk,
                null,
                steam,
                AntiCheatCheck.ByTitle(game));

            var actualKind = answer.Kind.ToString().ToLowerInvariant();
            Assert.Equal(row.GetProperty("expectedKind").GetString(), actualKind);

            var cls = AntiCheatClass.Classify(game, answer);
            var expectedClass = row.GetProperty("expectedClass").GetString()!;
            Assert.Equal(expectedClass, char.ToLowerInvariant(cls.ToString()[0]) + cls.ToString()[1..]);

            var wire = AntiCheatClass.WireClass(game, answer);
            Assert.Equal(row.GetProperty("expectedWireClass").GetString(), wire);

            var dllPolicy = AntiCheatClass.DllPolicyFor(cls);
            var dllCamel = char.ToLowerInvariant(dllPolicy.ToString()[0]) + dllPolicy.ToString()[1..];
            Assert.Equal(row.GetProperty("expectedDllPolicy").GetString(), dllCamel);

            var allowsExoFlow = cls != GameAntiCheatClass.CompetitivePvpOnline;
            Assert.Equal(row.GetProperty("allowsExoFlow").GetBoolean(), allowsExoFlow);

            Assert.Equal(
                row.GetProperty("allowsSettingsFile").GetBoolean(),
                AntiCheatCheck.Allows(game, answer, AntiCheatCheck.Change.GameSettingsFile));
            Assert.Equal(
                row.GetProperty("allowsFrameGenMod").GetBoolean(),
                AntiCheatCheck.Allows(game, answer, AntiCheatCheck.Change.FrameGenMod));
        }

        foreach (var row in doc.RootElement.GetProperty("refusedArguments").EnumerateArray())
        {
            var args = row.GetProperty("args").GetString();
            var expected = row.GetProperty("refused").ValueKind == JsonValueKind.Null ? null : row.GetProperty("refused").GetString();
            Assert.Equal(expected, AntiCheatCheck.RefusedArgument(args));
        }
    }
}
