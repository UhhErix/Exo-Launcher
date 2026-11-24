using System.Text.Json.Serialization;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal sealed class GameIncludeDto
{
    [JsonPropertyName("appId")]
    public string? AppId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

internal sealed class GameVariantDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("store")]
    public string Store { get; init; } = "";

    [JsonPropertyName("installed")]
    public bool Installed { get; init; }

    [JsonPropertyName("owned")]
    public bool Owned { get; init; }

    [JsonPropertyName("entitlementState")]
    public EntitlementState EntitlementState { get; init; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; init; }

    [JsonPropertyName("canInstall")]
    public bool CanInstall { get; init; }

    [JsonPropertyName("primaryAction")]
    public string? PrimaryAction { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("launchTarget")]
    public string? LaunchTarget { get; init; }

    [JsonPropertyName("playtimeMinutes")]
    public int PlaytimeMinutes { get; init; }

    [JsonPropertyName("lastPlayedUtc")]
    public string? LastPlayedUtc { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; init; }

    [JsonPropertyName("canStop")]
    public bool CanStop { get; init; }
}

internal sealed class GamePageDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("store")]
    public string Store { get; init; } = "";

    [JsonPropertyName("stores")]
    public string[] Stores { get; init; } = [];

    [JsonPropertyName("canonicalTitleKey")]
    public string? CanonicalTitleKey { get; init; }

    [JsonPropertyName("selectedVariantId")]
    public string? SelectedVariantId { get; init; }

    [JsonPropertyName("variants")]
    public GameVariantDto[] Variants { get; init; } = [];

    [JsonPropertyName("installed")]
    public bool Installed { get; init; }

    [JsonPropertyName("owned")]
    public bool Owned { get; init; }

    [JsonPropertyName("entitlementState")]
    public EntitlementState EntitlementState { get; init; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; init; }

    [JsonPropertyName("canInstall")]
    public bool CanInstall { get; init; }

    [JsonPropertyName("primaryAction")]
    public string? PrimaryAction { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("coverSource")]
    public string? CoverSource { get; init; }

    [JsonPropertyName("artRevision")]
    public long ArtRevision { get; init; }

    [JsonPropertyName("playtimeMinutes")]
    public int PlaytimeMinutes { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("sizeApproximate")]
    public bool SizeApproximate { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("deps")]
    public string[] Deps { get; init; } = [];

    [JsonPropertyName("launchNote")]
    public string LaunchNote { get; init; } = "";

    [JsonPropertyName("familyShared")]
    public bool FamilyShared { get; init; }

    [JsonPropertyName("sharedBy")]
    public string? SharedBy { get; init; }

    [JsonPropertyName("launchTarget")]
    public string? LaunchTarget { get; init; }

    [JsonPropertyName("includes")]
    public GameIncludeDto[]? Includes { get; init; }

    [JsonPropertyName("lastPlayedUtc")]
    public string? LastPlayedUtc { get; init; }

    [JsonPropertyName("isAddPortable")]
    public bool IsAddPortable { get; init; }

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; init; }

    [JsonPropertyName("canStop")]
    public bool CanStop { get; init; }

    [JsonPropertyName("buyUrl")]
    public string? BuyUrl { get; init; }

    [JsonPropertyName("tags")]
    public string[] Tags { get; init; } = [];

    [JsonPropertyName("antiCheatClass")]
    public string? AntiCheatClass { get; init; }
}

internal sealed class InstallProgressDto
{
    [JsonPropertyName("gameId")]
    public string? GameId { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    [JsonPropertyName("percent")]
    public double? Percent { get; init; }

    [JsonPropertyName("bytesDownloaded")]
    public long? BytesDownloaded { get; init; }

    [JsonPropertyName("bytesToDownload")]
    public long? BytesToDownload { get; init; }

    [JsonPropertyName("bytesPerSecond")]
    public double? BytesPerSecond { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("canCancel")]
    public bool CanCancel { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }
}

internal sealed class LibraryResponseDto
{
    [JsonPropertyName("games")]
    public IReadOnlyList<object> Games { get; init; } = [];

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("stores")]
    public object Stores { get; init; } = Array.Empty<object>();

    [JsonPropertyName("progress")]
    public InstallProgressDto? Progress { get; init; }

    [JsonPropertyName("queuedGameIds")]
    public IReadOnlyList<string> QueuedGameIds { get; init; } = [];

    [JsonPropertyName("pausedDownloads")]
    public IReadOnlyList<PausedDownload> PausedDownloads { get; init; } = [];

    [JsonPropertyName("sortMode")]
    public string SortMode { get; init; } = "";

    [JsonPropertyName("recent")]
    public IReadOnlyList<string> Recent { get; init; } = [];
}

internal sealed class GameGetReplyDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("game")]
    public GamePageDto? Game { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class DriveHeatDto
{
    [JsonPropertyName("peak")]
    public int Peak { get; init; }

    [JsonPropertyName("warn")]
    public int? Warn { get; init; }

    [JsonPropertyName("slowedSeconds")]
    public long SlowedSeconds { get; init; }
}

internal sealed class GameExtrasDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("achievementsUnlocked")]
    public int? AchievementsUnlocked { get; init; }

    [JsonPropertyName("achievementsTotal")]
    public int? AchievementsTotal { get; init; }

    [JsonPropertyName("driveHeat")]
    public DriveHeatDto? DriveHeat { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class AutopilotLiveDto
{
    [JsonPropertyName("game")]
    public string Game { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("seconds")]
    public double Seconds { get; init; }

    [JsonPropertyName("fps")]
    public double Fps { get; init; }

    [JsonPropertyName("low1")]
    public double Low1 { get; init; }

    [JsonPropertyName("low01")]
    public double Low01 { get; init; }

    [JsonPropertyName("stutters")]
    public int Stutters { get; init; }

    [JsonPropertyName("cpu")]
    public double? Cpu { get; init; }

    [JsonPropertyName("gpu")]
    public double? Gpu { get; init; }

    [JsonPropertyName("gpuTemp")]
    public double? GpuTemp { get; init; }

    [JsonPropertyName("vramGb")]
    public double? VramGb { get; init; }

    [JsonPropertyName("vramTotalGb")]
    public double? VramTotalGb { get; init; }

    [JsonPropertyName("ramGb")]
    public double? RamGb { get; init; }

    [JsonPropertyName("ramTotalGb")]
    public double? RamTotalGb { get; init; }

    [JsonPropertyName("standbyGb")]
    public double? StandbyGb { get; init; }

    [JsonPropertyName("freeGb")]
    public double? FreeGb { get; init; }

    [JsonPropertyName("dpc")]
    public double? Dpc { get; init; }

    [JsonPropertyName("clock")]
    public double? Clock { get; init; }
}

internal sealed class ShellBootstrapDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("artBase")]
    public object ArtBase { get; init; } = new();

    [JsonPropertyName("settings")]
    public object Settings { get; init; } = new();

    [JsonPropertyName("library")]
    public object Library { get; init; } = new();

    [JsonPropertyName("musicPlayback")]
    public object MusicPlayback { get; init; } = new();

    [JsonPropertyName("discordStatus")]
    public object DiscordStatus { get; init; } = new();

    [JsonPropertyName("binaryMediaPipe")]
    public string? BinaryMediaPipe { get; init; }
}
