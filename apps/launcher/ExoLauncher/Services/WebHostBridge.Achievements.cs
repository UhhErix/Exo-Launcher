using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private void OnAchievementUnlocked(Unlock unlock)
    {
        if (_detached || _transport is not NamedPipeHostTransport) return;
        var settings = _services.Settings.Current;
        if (!settings.AchievementBanners && unlock.GameId != "preview") return;
        var tier = unlock.Completed ? "completion" : unlock.Rarity;
        PostEvent("achievement.unlocked", new
        {
            game = unlock.Game,
            name = unlock.Name,
            description = unlock.Description,
            icon = unlock.Icon?.StartsWith("data:image/", StringComparison.Ordinal) == true ? unlock.Icon : null,
            tier,
            percent = unlock.Percent,
            unlocked = unlock.Done,
            total = unlock.Total,
        });
        AchievementBanner.PlayTierSound(tier, settings.AchievementVolume);
    }
}
