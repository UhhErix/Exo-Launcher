using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public class MousePanelRelayTests
{
    [Theory]
    [InlineData("hid.list")]
    [InlineData("hid.open")]
    [InlineData("hid.close")]
    [InlineData("hid.sendReport")]
    [InlineData("hid.sendFeatureReport")]
    [InlineData("hid.receiveFeatureReport")]
    [InlineData("hid.receiveInputReport")]
    [InlineData("exo.games")]
    [InlineData("exo.profiles.read")]
    [InlineData("exo.profiles.write")]
    [InlineData("exo.presets.read")]
    [InlineData("exo.presets.write")]
    [InlineData("exo.textScale")]
    [InlineData("exo.motion")]
    [InlineData("exo.theme")]
    [InlineData("exo.mouseCore")]
    public void AllowList_PermitsExactSixteenRelayMethods(string method)
    {
        Assert.True(MousePanelRelay.IsAllowedMethod(method));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("settings.set")]
    [InlineData("game.launch")]
    [InlineData("mouse.panel.open")]
    [InlineData("mouse.panel.close")]
    [InlineData("system.mouseCore")]
    [InlineData("hid.reset")]
    [InlineData("exo.settings")]
    [InlineData("HID.LIST")]
    public void AllowList_RejectsEverythingOutsideRelayBoundary(string? method)
    {
        Assert.False(MousePanelRelay.IsAllowedMethod(method));
    }

    [Theory]
    [InlineData("measure")]
    [InlineData("drivers")]
    [InlineData("spread")]
    [InlineData("unspread")]
    [InlineData("apply")]
    [InlineData("undo")]
    public void ValidateCoreAction_AllowsSixMouseCoreActions(string action)
    {
        using var doc = JsonDocument.Parse($"{{\"action\":\"{action}\"}}");
        Assert.Equal(action, MousePanelRelay.ValidateCoreAction(doc.RootElement, true));
    }

    [Fact]
    public void ValidateCoreAction_AllowsMissingOrNullActionForRead()
    {
        using var empty = JsonDocument.Parse("{}");
        using var nulled = JsonDocument.Parse("{\"action\":null}");
        Assert.Null(MousePanelRelay.ValidateCoreAction(empty.RootElement, true));
        Assert.Null(MousePanelRelay.ValidateCoreAction(empty.RootElement, false));
        Assert.Null(MousePanelRelay.ValidateCoreAction(nulled.RootElement, true));
    }

    [Theory]
    [InlineData("{\"action\":\"kill\"}")]
    [InlineData("{\"action\":\"\"}")]
    [InlineData("{\"action\":123}")]
    [InlineData("{\"action\":\"APPLY\"}")]
    public void ValidateCoreAction_ThrowsForUnknownAction(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MousePanelRelay.ValidateCoreAction(doc.RootElement, true));
        Assert.Equal("Exo Mouse asked for an unknown CPU core action.", ex.Message);
    }

    [Theory]
    [InlineData("https://app.exo-launcher.local/mouse-panel.html", true)]
    [InlineData("https://app.exo-launcher.local/MOUSE-PANEL.HTML", true)]
    [InlineData("https://mouse.exo-launcher.local/index.html", false)]
    [InlineData("https://app.exo-launcher.local/index.html", false)]
    [InlineData("https://example.com/mouse-panel.html", false)]
    [InlineData(null, false)]
    public void IsTrustedWrapperSource_OnlyTrustsWrapperPage(string? source, bool expected)
    {
        Assert.Equal(expected, MousePanelRelay.IsTrustedWrapperSource(source));
    }

    [Theory]
    [InlineData("small", 0.9, "0.9")]
    [InlineData("default", 1.0, "1")]
    [InlineData("large", 1.14, "1.14")]
    [InlineData(null, 1.0, "1")]
    [InlineData("unknown", 1.0, "1")]
    public void TextScaleFor_MapsLauncherTextSize(string? setting, double expected, string expectedStr)
    {
        Assert.Equal(expected, MousePanelRelay.TextScaleFor(setting));
        Assert.Equal(expectedStr, MousePanelRelay.TextScaleString(setting));
    }

    [Fact]
    public void MotionFor_MapsReduceMotionFlag()
    {
        Assert.Equal("reduce", MousePanelRelay.MotionFor(true));
        Assert.Equal("full", MousePanelRelay.MotionFor(false));
    }

    [Theory]
    [InlineData(1920.0, 1.0, 413)]
    [InlineData(2560.0, 1.0, 550)]
    [InlineData(3840.0, 1.0, 560)]
    [InlineData(1200.0, 1.0, 340)]
    [InlineData(1920.0, 1.14, 413)]
    [InlineData(1200.0, 1.14, 388)]
    [InlineData(1200.0, 0.9, 306)]
    public void RailWidth_MatchesDevicesRailFormula(double roomWidth, double scale, int expected)
    {
        Assert.Equal(expected, MousePanelRelay.RailWidth(roomWidth, scale));
    }

    [Fact]
    public void BuildLookTokens_PassesHostLookSanitizerForAllTwentyNineTokens()
    {
        var tokens = MousePanelRelay.BuildLookTokens();
        Assert.Equal(29, tokens.Count);
        var forbidden = new Regex(@"[;{}]|url\(|expression\(|var\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var (key, value) in tokens)
        {
            Assert.StartsWith("--", key, StringComparison.Ordinal);
            Assert.True(value.Length is > 0 and <= 400, $"{key} has invalid length {value.Length}");
            Assert.False(forbidden.IsMatch(value), $"{key} failed host-look plain() check: {value}");
        }
    }

    [Fact]
    public void BuildInstalledGames_FiltersMapsVariantsConvertsLocalCoversAndSorts()
    {
        var library = new List<GameEntry>
        {
            new()
            {
                Id = "steam:2",
                Store = StoreKind.Steam,
                Title = "Zelda Clone",
                Installed = true,
                CoverUrl = "https://covers.exo-launcher.local/steam_2.webp?v=3",
                Variants = [new GameVariant { Id = "epic:2", Store = StoreKind.Epic }],
            },
            new()
            {
                Id = "steam:1",
                Store = StoreKind.Steam,
                Title = "Apex Legends",
                Installed = true,
                CoverUrl = "https://remote.example.com/apex.png",
            },
            new()
            {
                Id = "steam:3",
                Store = StoreKind.Steam,
                Title = "Not Installed",
                Installed = false,
                CoverUrl = "https://covers.exo-launcher.local/steam_3.webp",
                Variants = [new GameVariant { Id = "epic:3", Store = StoreKind.Epic }],
            },
        };

        var (games, cardMap) = MousePanelRelay.BuildInstalledGames(library, file => $"https://thumbs.local/{file}");

        Assert.Equal(2, games.Count);
        Assert.Equal("Apex Legends", games[0].Title);
        Assert.Null(games[0].Cover);
        Assert.Equal("Zelda Clone", games[1].Title);
        Assert.Equal("https://thumbs.local/steam_2.webp", games[1].Cover);
        Assert.Equal("steam:2", cardMap["epic:2"]);
        Assert.Equal("steam:3", cardMap["epic:3"]);
    }

    [Fact]
    public void ValidateNotice_EnforcesNonEmptyAndMaxLength()
    {
        using var ok = JsonDocument.Parse("{\"notice\":\"Profile saved\"}");
        using var empty = JsonDocument.Parse("{\"notice\":\"\"}");
        using var tooLong = JsonDocument.Parse($"{{\"notice\":\"{new string('a', 161)}\"}}");
        Assert.Equal("Profile saved", MousePanelRelay.ValidateNotice(ok.RootElement));
        Assert.Null(MousePanelRelay.ValidateNotice(empty.RootElement));
        Assert.Null(MousePanelRelay.ValidateNotice(tooLong.RootElement));
    }

    [Fact]
    public void TryParseMouseState_ValidatesImageHostAndClampsFields()
    {
        using var valid = JsonDocument.Parse(
            "{\"state\":{\"name\":\"Starlight X\",\"maker\":\" Finalmouse \",\"battery\":87.6,\"charging\":false,\"low\":false,\"wireless\":true,\"image\":\"https://img.openmouse.app/starlight-x.webp\"}}");
        Assert.True(MousePanelRelay.TryParseMouseState(valid.RootElement, out var state));
        Assert.NotNull(state);
        Assert.Equal("Starlight X", state!.Name);
        Assert.Equal("Finalmouse", state.Maker);
        Assert.Equal(88, state.Battery);
        Assert.Equal("https://img.openmouse.app/starlight-x.webp", state.Image);
        Assert.True(state.Wireless);

        using var badImage = JsonDocument.Parse(
            "{\"state\":{\"name\":\"Starlight X\",\"battery\":150,\"image\":\"https://evil.example/x.png\"}}");
        Assert.True(MousePanelRelay.TryParseMouseState(badImage.RootElement, out var sanitized));
        Assert.NotNull(sanitized);
        Assert.Null(sanitized!.Battery);
        Assert.Null(sanitized.Image);

        using var cleared = JsonDocument.Parse("{\"state\":null}");
        Assert.True(MousePanelRelay.TryParseMouseState(cleared.RootElement, out var nulled));
        Assert.Null(nulled);
    }

    [Fact]
    public void WrapperHtml_FramesMouseOriginOverPostMessageWithoutInlineMouseCode()
    {
        Assert.Contains(MousePanelRelay.MouseStartUrl, MousePanelRelay.WrapperHtml, StringComparison.Ordinal);
        Assert.Contains(MousePanelRelay.MouseOrigin, MousePanelRelay.WrapperHtml, StringComparison.Ordinal);
        Assert.Contains("window.chrome.webview.postMessage", MousePanelRelay.WrapperHtml, StringComparison.Ordinal);
    }
}
