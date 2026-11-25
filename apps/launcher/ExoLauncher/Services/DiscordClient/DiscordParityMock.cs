using System.Text.Json;

namespace ExoLauncher.Services.DiscordClient;

internal static class DiscordParityMock
{
    private const string MeId = "100000000000000001";
    private const string AdaId = "100000000000000002";
    private const string KaiId = "100000000000000003";
    private const string RowanId = "100000000000000021";
    private const string JunoId = "100000000000000022";
    private const string MiraId = "100000000000000024";
    private const string GuildId = "300000000000000001";
    private const string Guild2Id = "300000000000000002";
    private const string Guild3Id = "300000000000000003";
    private const string DmAdaId = "200000000000000001";
    private const string DmGroupId = "200000000000000002";
    private const string DmRowanId = "200000000000000021";
    private const string DmJunoId = "200000000000000022";
    private const string DmMiraId = "200000000000000024";
    private const string ChanWelcomeId = "400000000000000001";
    private const string ChanGeneralId = "400000000000000002";
    private const string ChanModId = "400000000000000003";
    private const string ChanVoiceId = "400000000000000005";
    private const string ChanScrimsId = "400000000000000006";

    private static readonly Lock Gate = new();
    private static string _status = "online";
    private static string _voiceState = "off";
    private static string? _voiceGuildId;
    private static string? _voiceChannelId;
    private static bool _muted;
    private static bool _deafened;

    internal static bool IsEnabled =>
        Environment.GetEnvironmentVariable("EXO_E2E_TEST_INSTANCE") == "1"
        && Environment.GetEnvironmentVariable("EXO_DISCORD_PARITY_MOCK") == "1";

    internal static bool TryHandle(string method, JsonElement p, bool hasParams, out object? result)
    {
        result = null;
        if (!IsEnabled) return false;
        if (method == "friends.badge")
        {
            result = new { count = 3 };
            return true;
        }
        if (!method.StartsWith("discord.", StringComparison.Ordinal)) return false;

        result = method switch
        {
            "discord.status" => new { state = "ready", message = (string?)null, signedIn = true },
            "discord.accounts" => new
            {
                ok = true,
                accounts = new[]
                {
                    new { id = MeId, name = "You", avatar = (string?)null, current = true },
                },
            },
            "discord.home" => BuildHome(),
            "discord.friends" => BuildFriends(),
            "discord.channels" => BuildChannels(ReadStr(p, hasParams, "guildId")),
            "discord.messages" => BuildMessages(ReadStr(p, hasParams, "channelId")),
            "discord.members" => BuildMembers(),
            "discord.profile" => BuildProfile(ReadStr(p, hasParams, "userId"), ReadBool(p, hasParams, "mutualFriends") == true),
            "discord.unreads" => new { channels = Array.Empty<object>() },
            "discord.voice" => HandleVoice(p, hasParams),
            "discord.audio" => BuildAudio(),
            "discord.stream" => new { ok = true, active = false, streams = Array.Empty<object>() },
            "discord.golive" => HandleGoLive(p, hasParams),
            "discord.clientSettings" => BuildClientSettings(),
            "discord.userSettings" => new { ok = true, message = (string?)null, values = new { status = _status, developerMode = false } },
            "discord.friend" => new { ok = true, relationship = 1, ignored = false, bot = false, stranger = false },
            "discord.account" => BuildAccount(ReadStr(p, hasParams, "action")),
            "discord.setStatus" => HandleSetStatus(p, hasParams),
            "discord.colors" => new { colors = new Dictionary<string, string?>() },
            "discord.mentionable" => new { users = Array.Empty<object>(), roles = Array.Empty<object>(), channels = Array.Empty<object>() },
            "discord.emojis" => new { guilds = Array.Empty<object>() },
            "discord.stickers" => new { ok = true, packs = Array.Empty<object>(), guild = Array.Empty<object>() },
            "discord.stickerData" => new { ok = false, message = "No sticker data in parity mock." },
            "discord.pins" => new { ok = true, messages = Array.Empty<object>() },
            "discord.callSongs" => new { songs = Array.Empty<object>() },
            "discord.apps" => new { ok = true, commands = Array.Empty<object>(), applications = Array.Empty<object>() },
            "discord.soundboard" => new { ok = true, sounds = Array.Empty<object>(), defaults = Array.Empty<object>() },
            "discord.gifFavorites" => new { ok = true, gifs = Array.Empty<object>() },
            "discord.emojiFavorites" => new { ok = true, favorites = Array.Empty<string>(), frequent = Array.Empty<string>(), reactions = Array.Empty<string>() },
            "discord.mentions" => new { ok = true, messages = Array.Empty<object>() },
            "discord.saved" => new { ok = true, items = Array.Empty<object>() },
            "discord.forYou" => new { ok = true, items = Array.Empty<object>(), hasMore = false },
            "discord.threads" => new { ok = true, threads = Array.Empty<object>() },
            "discord.micMonitor" => new { ok = true, on = false, onHead = false },
            "discord.myVoice" => new { ok = true, learned = false, message = (string?)null },
            "discord.shop" => new { ok = true, categories = Array.Empty<object>(), owned = Array.Empty<object>() },
            "discord.ack" or "discord.markRead" or "discord.view" or "discord.typing" or "discord.camera" or "discord.micTest" or "discord.sound" or "discord.channel" or "discord.server" or "discord.signIn" or "discord.signOut" or "discord.ring" => new { ok = true },
            _ => new { ok = true },
        };
        return true;
    }

    private static object BuildHome()
    {
        const long fixedBaseMs = 1795500000000L;
        string status;
        lock (Gate) status = _status;
        return new
        {
            me = new { id = MeId, name = "You", avatar = (string?)null, status },
            dms = new object[]
            {
                new { id = DmAdaId, name = "Ada", avatar = (string?)null, group = false, unread = true, mentions = 2, userId = AdaId, username = "ada", status = "online", lastAt = fixedBaseMs - 180_000, line = "I can do 9pm or maybe earlier if the others are up" },
                new
                {
                    id = DmGroupId,
                    name = "Kai, Ada",
                    avatar = (string?)null,
                    group = true,
                    unread = false,
                    mentions = 0,
                    ownerId = MeId,
                    subtitle = "3 Members",
                    lastAt = fixedBaseMs - 840_000,
                    line = "Kai: the new patch broke my sens",
                    members = new object[]
                    {
                        new { id = AdaId, name = "Ada", username = "ada", avatar = (string?)null, status = "online" },
                        new { id = KaiId, name = "Kai", username = "kai", avatar = (string?)null, status = "idle" },
                    },
                },
                new { id = DmRowanId, name = "Rowan", avatar = (string?)null, group = false, unread = false, mentions = 0, userId = RowanId, username = "rowan", status = "online", lastAt = fixedBaseMs - 2_520_000, line = "You: send me your config when you get a sec" },
                new { id = DmJunoId, name = "Juno", avatar = (string?)null, group = false, unread = true, mentions = 0, userId = JunoId, username = "juno", status = "idle", lastAt = fixedBaseMs - 5_760_000, line = "did you see the patch notes" },
                new { id = DmMiraId, name = "Mira", avatar = (string?)null, group = false, unread = false, mentions = 0, userId = MiraId, username = "mira", status = "offline", lastAt = fixedBaseMs - 90_000_000, line = "night!" },
            },
            guilds = new object[]
            {
                new { id = GuildId, name = "Night Raid", icon = (string?)null, muted = false, unread = true, mentions = 1, inVoice = 2, owner = true },
                new { id = Guild2Id, name = "Frame Pacing Club", icon = (string?)null, muted = false, unread = false, mentions = 0, inVoice = 3 },
                new { id = Guild3Id, name = "Speedrun Lab", icon = (string?)null, muted = true, unread = false, mentions = 0 },
            },
            rail = new object[]
            {
                new { guildIds = new[] { GuildId } },
                new { guildIds = new[] { Guild2Id } },
                new { guildIds = new[] { Guild3Id } },
            },
        };
    }

    private static object BuildFriends() => new
    {
        friends = new object[]
        {
            new { id = AdaId, name = "Ada", username = "ada", avatar = (string?)null, kind = "friend", status = "online", activity = "VALORANT", activityType = 0, dmId = DmAdaId },
            new { id = RowanId, name = "Rowan", username = "rowan", avatar = (string?)null, kind = "friend", status = "online", activity = "Marvel Rivals", activityType = 0, dmId = DmRowanId },
            new { id = KaiId, name = "Kai", username = "kai", avatar = (string?)null, kind = "friend", status = "idle", activity = "Counter-Strike 2", activityType = 0 },
            new { id = JunoId, name = "Juno", username = "juno", avatar = (string?)null, kind = "friend", status = "idle", dmId = DmJunoId },
            new { id = MiraId, name = "Mira", username = "mira", avatar = (string?)null, kind = "friend", status = "offline", dmId = DmMiraId },
        },
        restricted = new { blocked = Array.Empty<object>(), ignored = Array.Empty<object>() },
    };

    private static object BuildChannels(string? guildId)
    {
        var name = guildId switch
        {
            Guild2Id => "Frame Pacing Club",
            Guild3Id => "Speedrun Lab",
            _ => "Night Raid",
        };
        return new
        {
            name,
            canManage = true,
            canManageEvents = true,
            channels = new object[]
            {
                new { id = ChanWelcomeId, name = "welcome", type = 0, kind = "text", topic = "Scrims are Tuesdays at 8", canRead = true, canSend = true },
                new { id = "400000000000000009", name = "Text", type = 4, kind = "category" },
                new { id = ChanGeneralId, name = "general", type = 0, kind = "text", topic = "Talk about anything. Be kind.", canRead = true, canSend = true, unread = true, mentions = 1, canManageMessages = true, canPin = true, canEdit = true },
                new { id = ChanModId, name = "mod-chat", type = 0, kind = "text", canRead = true, canSend = true, @private = true, locked = true },
                new { id = "400000000000000008", name = "Voice", type = 4, kind = "category" },
                new
                {
                    id = ChanVoiceId,
                    name = "Lobby",
                    type = 2,
                    kind = "voice",
                    canConnect = true,
                    userLimit = 10,
                    members = new object[]
                    {
                        new { id = AdaId, name = "Ada", avatar = (string?)null, mute = false, deaf = false },
                        new { id = KaiId, name = "Kai", avatar = (string?)null, mute = true, deaf = false },
                    },
                },
                new { id = ChanScrimsId, name = "Scrims", type = 2, kind = "voice", canConnect = true, locked = true },
            },
        };
    }

    private static object BuildMessages(string? channelId)
    {
        var cid = string.IsNullOrEmpty(channelId) ? DmAdaId : channelId;
        var meAuthor = new { id = MeId, username = "you", global_name = "You", avatar = (string?)null };
        var adaAuthor = new { id = AdaId, username = "ada", global_name = "Ada", avatar = (string?)null };
        var kaiAuthor = new { id = KaiId, username = "kai", global_name = "Kai", avatar = (string?)null };
        var msgs = new object[]
        {
            new { id = "1310000000000000005", channel_id = cid, type = 0, author = adaAuthor, content = "I can do 9pm or maybe earlier if the others are up", timestamp = "2026-11-24T18:55:00.000Z", attachments = Array.Empty<object>(), embeds = Array.Empty<object>(), mentions = Array.Empty<object>() },
            new { id = "1310000000000000004", channel_id = cid, type = 0, author = kaiAuthor, content = "same, just finished updating drivers", timestamp = "2026-11-24T18:50:00.000Z", attachments = Array.Empty<object>(), embeds = Array.Empty<object>(), mentions = Array.Empty<object>() },
            new { id = "1310000000000000003", channel_id = cid, type = 0, author = meAuthor, content = "sounds good, see you in Lobby", timestamp = "2026-11-24T18:45:00.000Z", attachments = Array.Empty<object>(), embeds = Array.Empty<object>(), mentions = Array.Empty<object>() },
            new { id = "1310000000000000002", channel_id = cid, type = 0, author = adaAuthor, content = "anyone up for ranked tonight?", timestamp = "2026-11-24T18:40:00.000Z", attachments = Array.Empty<object>(), embeds = Array.Empty<object>(), mentions = Array.Empty<object>() },
        };
        return new { ok = true, message = (string?)null, messages = msgs, lastRead = "1310000000000000003" };
    }

    private static object BuildMembers() => new
    {
        total = 4,
        groups = new object[]
        {
            new
            {
                id = "founder",
                name = "Founder",
                count = 1,
                members = new object[]
                {
                    new { id = MeId, name = "You", username = "you", avatar = (string?)null, status = "online" },
                },
            },
            new
            {
                id = "online",
                name = "Online",
                count = 2,
                members = new object[]
                {
                    new { id = AdaId, name = "Ada", username = "ada", avatar = (string?)null, status = "online", activity = "VALORANT" },
                    new { id = RowanId, name = "Rowan", username = "rowan", avatar = (string?)null, status = "online", activity = "Marvel Rivals" },
                },
            },
            new
            {
                id = "offline",
                name = "Offline",
                count = 1,
                members = new object[]
                {
                    new { id = MiraId, name = "Mira", username = "mira", avatar = (string?)null, status = "offline" },
                },
            },
        },
    };

    private static object BuildProfile(string? userId, bool mutualFriends)
    {
        if (mutualFriends)
        {
            return new
            {
                ok = true,
                friends = new object[]
                {
                    new { id = KaiId, name = "Kai", username = "kai", avatar = (string?)null },
                    new { id = RowanId, name = "Rowan", username = "rowan", avatar = (string?)null },
                },
            };
        }
        return new
        {
            ok = true,
            id = userId ?? AdaId,
            name = "Ada",
            username = "ada",
            avatar = (string?)null,
            bio = "FPS player & frame-time nerd",
            createdAt = 1609459200000L,
            note = "",
            mutualGuilds = new object[]
            {
                new { id = GuildId, name = "Night Raid", icon = (string?)null },
            },
        };
    }

    private static object HandleVoice(JsonElement p, bool hasParams)
    {
        lock (Gate)
        {
            switch (ReadStr(p, hasParams, "action"))
            {
                case "join":
                    _voiceState = "connected";
                    _voiceGuildId = ReadStr(p, hasParams, "guildId") ?? GuildId;
                    _voiceChannelId = ReadStr(p, hasParams, "channelId") ?? ChanVoiceId;
                    break;
                case "leave":
                    _voiceState = "off";
                    _voiceGuildId = null;
                    _voiceChannelId = null;
                    break;
                case "set":
                    if (ReadBool(p, hasParams, "muted") is { } m) _muted = m;
                    if (ReadBool(p, hasParams, "deafened") is { } d) _deafened = d;
                    break;
            }
            return new
            {
                ok = true,
                voice = new
                {
                    state = _voiceState,
                    guildId = _voiceGuildId,
                    channelId = _voiceChannelId,
                    muted = _muted,
                    deafened = _deafened,
                    e2ee = _voiceState == "connected",
                },
            };
        }
    }

    private static object BuildAudio() => new
    {
        inputs = new[] { new { id = "default", name = "Default Microphone" } },
        outputs = new[] { new { id = "default", name = "Default Headphones" } },
        input = "default",
        output = "default",
        inputVolume = 100,
        outputVolume = 100,
        soundboardVolume = 80,
        soundboardMuted = false,
    };

    private static object HandleGoLive(JsonElement p, bool hasParams)
    {
        if (ReadStr(p, hasParams, "action") == "sources")
        {
            return new
            {
                ok = true,
                message = (string?)null,
                captureMode = "wgc",
                serviceDisabled = false,
                sources = new object[]
                {
                    new { id = "screen:1", kind = "screen", title = "Display 1", app = "Display 1", icon = (string?)null, thumb = (string?)null, game = (string?)null, width = 1920, height = 1080, hz = 240, primary = true },
                    new { id = "window:1", kind = "window", title = "VALORANT", app = "VALORANT", icon = (string?)null, thumb = (string?)null, game = (string?)"VALORANT", width = 1920, height = 1080, hz = 240, primary = false },
                },
            };
        }
        return new { ok = true, message = (string?)null };
    }

    private static object BuildClientSettings() => new
    {
        inputMode = "vad",
        vadAuto = true,
        vadThreshold = -40,
        pttKey = (string?)null,
        pttReleaseMs = 20,
        inputProfile = "voice",
        noiseSuppression = true,
        echoCancellation = true,
        autoGain = true,
        attenuation = 0,
        attenuationMe = false,
        attenuationOthers = false,
        othersNoiseSuppression = false,
        muteWhenInactive = false,
        streamerMode = false,
        hideInvites = true,
        hideNotifications = true,
        sounds = true,
        keybinds = Array.Empty<object>(),
    };

    private static object BuildAccount(string? action)
    {
        if (action == "me")
        {
            return new
            {
                ok = true,
                me = new
                {
                    id = MeId,
                    username = "you",
                    globalName = "You",
                    avatar = (string?)null,
                    banner = (string?)null,
                    accentColor = (int?)null,
                    bio = "Parity test profile",
                    email = "y***@example.com",
                    phone = (string?)null,
                    mfa = true,
                    verified = true,
                    nitro = 0,
                },
            };
        }
        if (action == "tags")
        {
            return new { ok = true, mine = (string?)null, servers = Array.Empty<object>() };
        }
        if (action == "consent")
        {
            return new { ok = true, data = new { usageStatistics = false, personalization = false } };
        }
        return new { ok = true, data = Array.Empty<object>() };
    }

    private static object HandleSetStatus(JsonElement p, bool hasParams)
    {
        if (ReadStr(p, hasParams, "status") is { Length: > 0 } next)
        {
            lock (Gate) _status = next;
        }
        return new { ok = true };
    }

    private static string? ReadStr(JsonElement p, bool hasParams, string name) =>
        hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? ReadBool(JsonElement p, bool hasParams, string name) =>
        hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
