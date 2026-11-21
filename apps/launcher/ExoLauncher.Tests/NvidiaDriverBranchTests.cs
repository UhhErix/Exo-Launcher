using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the card's driver branch could be read wrong (System > Graphics then offers a hotfix that NVIDIA's installer refuses,
/// as it did to a GTX 1080 Ti, or holds a newer card back from its hotfix):
/// - a Maxwell, Pascal or Volta card (left on release 580) is taken for one on NVIDIA's current branches;
/// - a Turing or newer card is taken for a left one;
/// - Windows' hardware id is read case-sensitively or with its subsystem part in the way;
/// - an unreadable or non-NVIDIA id counts as a current card.
/// </summary>
public sealed class NvidiaDriverBranchTests
{
    [Theory]
    [InlineData(@"pci\ven_10de&dev_1b06")] // GeForce GTX 1080 Ti (Pascal)
    [InlineData(@"pci\ven_10de&dev_13c2")] // GeForce GTX 970 (Maxwell)
    [InlineData(@"pci\ven_10de&dev_1d81")] // TITAN V (Volta)
    public void Cards_Game_Ready_left_get_no_hotfix(string hardwareId) =>
        Assert.False(NvidiaDriverService.CurrentBranches(hardwareId));

    [Theory]
    [InlineData(@"pci\ven_10de&dev_1e87")] // GeForce RTX 2080 (Turing)
    [InlineData(@"pci\ven_10de&dev_2484")] // GeForce RTX 3070 (Ampere)
    [InlineData(@"PCI\VEN_10DE&DEV_2484&SUBSYS_146B10DE")]
    public void Turing_and_newer_are_on_the_current_branches(string hardwareId) =>
        Assert.True(NvidiaDriverService.CurrentBranches(hardwareId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"pci\ven_1002&dev_73bf")]
    public void An_unknown_card_gets_no_hotfix(string? hardwareId) =>
        Assert.False(NvidiaDriverService.CurrentBranches(hardwareId));

    [Fact]
    public void Hotfixes_are_enabled_for_GeForce_Game_Ready_on_current_branches_only()
    {
        Assert.True(NvidiaDriverService.OfferHotfixes);
        Assert.True(NvidiaDriverService.ChecksHotfix("NVIDIA GeForce RTX 3070", workstation: false, studio: false, @"PCI\VEN_10DE&DEV_2484"));
        Assert.True(NvidiaDriverService.ChecksHotfix("NVIDIA GeForce RTX 5080", workstation: false, studio: false, @"PCI\VEN_10DE&DEV_2C02"));
        Assert.False(NvidiaDriverService.ChecksHotfix("NVIDIA GeForce RTX 3070", workstation: false, studio: true, @"PCI\VEN_10DE&DEV_2484"));
        Assert.False(NvidiaDriverService.ChecksHotfix("NVIDIA RTX A4000", workstation: true, studio: false, @"PCI\VEN_10DE&DEV_24B0"));
        Assert.False(NvidiaDriverService.ChecksHotfix("NVIDIA GeForce GTX 1080 Ti", workstation: false, studio: false, @"PCI\VEN_10DE&DEV_1B06"));
    }

    [Fact]
    public void Hotfix_candidates_and_url_cover_newer_versions()
    {
        var candidates = NvidiaDriverService.HotfixCandidates("621.24");
        Assert.Contains("621.35", candidates);
        Assert.Contains("622.02", candidates);
        Assert.DoesNotContain("621.24", candidates);
        Assert.Equal("https://international.download.nvidia.com/Windows/621.35hf/621.35-desktop-notebook-win10-win11-64bit-international-dch.hf.exe",
            NvidiaDriverService.HotfixUrl("621.35"));
    }

    [Fact]
    public void Hotfix_forum_topics_only_read_official_staff_posts()
    {
        const string search = """
            {"data":{"topics":[
              {"id":101,"title":"Community hotfix question","createdStamp":2000,"user":{"aclactions":{"officialreply":false}}},
              {"id":102,"title":"Announcing GeForce Hotfix Driver 621.35","createdStamp":3000,"user":{"aclactions":{"officialreply":true}}},
              {"id":103,"title":"Game Ready Feedback","createdStamp":4000,"user":{"aclactions":{"officialreply":true}}}
            ]}}
            """;
        Assert.Equal([102L], NvidiaDriverService.HotfixTopics(search));

        const string topic = """
            {"data":{"topic":{"firstComment":{"body":"Download [here](https://international.download.nvidia.com/Windows/621.35hf/621.35-desktop-notebook-win10-win11-64bit-international-dch.hf.exe)"}}}}
            """;
        var parsed = NvidiaDriverService.HotfixIn(topic);
        Assert.NotNull(parsed);
        Assert.Equal(("621.35", "hotfix"), (parsed.Version, parsed.Kind));
    }
}
