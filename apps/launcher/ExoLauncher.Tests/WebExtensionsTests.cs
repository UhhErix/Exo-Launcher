using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the bundled provider extensions (Exo Shield, SponsorBlock) could fail:
/// - a file in an extension's folder changes (an update, or a note added beside it) without its pinned hash, so Exo
///   refuses to install it on every PC that installs fresh, silently;
/// - an extension listed in WebExtensions has no folder, or no manifest, in Assets\Extensions;
/// - what the browser writes into the folder after loading it (an MV3 blocker's _metadata) counts toward the hash, so the
///   next check on that PC refuses an extension that hasn't changed;
/// - a retired extension (uBlock Origin Lite, which Exo Shield replaced, or plain uBlock Origin) is not caught by its
///   name or its marker's id, so it keeps running beside Exo Shield in the embedded profile;
/// - a shipped extension's name falls under a retired rule ("Exo Shield", or a name that only starts with
///   "uBlock Origin"), so every start removes it and installs it again.
/// Exo Shield's own rules are checked in ExoShieldTests.
/// </summary>
public sealed class WebExtensionsTests
{
    private static string Bundled(string folder)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ExoLauncher", "Assets", "Extensions", folder);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Assets\\Extensions\\" + folder + " is not in the repository.");
    }

    [Fact]
    public void EveryBundledExtensionMatchesItsPinnedHash()
    {
        foreach (var entry in WebExtensions.Entries)
        {
            var folder = Bundled(entry.Folder);
            Assert.True(File.Exists(Path.Combine(folder, "manifest.json")), entry.Title + " has no manifest.json");
            Assert.Equal(entry.ManifestSha256, WebExtensionManifest.ComputeManifestHash(folder));
        }
    }

    [Fact]
    public void WhatTheBrowserWritesAfterLoadingLeavesTheHashAlone()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "rules"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"manifest_version\":3}");
        File.WriteAllText(Path.Combine(folder, "rules", "a.json"), "[]");
        var before = WebExtensionManifest.ComputeManifestHash(folder);

        Directory.CreateDirectory(Path.Combine(folder, "_metadata", "generated_indexed_rulesets"));
        File.WriteAllBytes(Path.Combine(folder, "_metadata", "generated_indexed_rulesets", "_ruleset1"), [1, 2, 3]);
        Assert.Equal(before, WebExtensionManifest.ComputeManifestHash(folder));

        // The extension's own files still count, wherever they are.
        File.WriteAllText(Path.Combine(folder, "rules", "a.json"), "[{}]");
        Assert.NotEqual(before, WebExtensionManifest.ComputeManifestHash(folder));
        DeletionGate.DeleteDirectory(folder, recursive: true);
    }

    [Fact]
    public void RetiredExtensionsGoAndShippedOnesStay()
    {
        var none = new HashSet<string>();
        Assert.True(WebExtensions.Retires("uBlock Origin Lite", "lite-id", none));
        Assert.True(WebExtensions.Retires("uBlock Origin", "mv2-id", none));
        // Its marker's id catches it whatever name it reports (a raw __MSG_ key included).
        Assert.True(WebExtensions.Retires("__MSG_extName__", "lite-id", new HashSet<string> { "lite-id" }));
        Assert.False(WebExtensions.Retires("uBlock Origin Extra", "other-id", none));
        foreach (var entry in WebExtensions.Entries)
        {
            Assert.False(WebExtensions.Retires(entry.DisplayName, "shipped-id", none), entry.Title + " would be removed every start");
            Assert.False(WebExtensions.Retires(entry.Title, "shipped-id", none), entry.Title + " would be removed every start");
        }
    }
}
