using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What the room's Home and Servers pages show of right now, from what the connection already holds: the last thing
/// said in a DM, the newest mention in a channel, and who is in voice. Nothing here asks Discord for anything, except
/// one read of Mentions per connection (the bridge's, when a mention landed while Exo was away).
/// </summary>
internal sealed partial class DiscordCache
{
    /// <summary>The most voice rooms the room is told about, and the faces it is given for each.</summary>
    private const int MostRooms = 60, MostFaces = 8;

    /// <summary>A message as one plain line: names for mentions, emoji by name, spoilers hidden, no markup; what it holds when it has no words.</summary>
    private string PlainLine(JsonElement d, int most)
    {
        var text = Str(d, "content") ?? "";
        foreach (var user in Array(d, "mentions"))
            if (Str(user, "id") is { } uid)
                text = text.Replace($"<@{uid}>", "@" + ReadUser(user).Name).Replace($"<@!{uid}>", "@" + ReadUser(user).Name);
        text = Regex.Replace(text, "<a?:(\\w+):\\d+>", ":$1:");
        text = Regex.Replace(text, "\\|\\|.+?\\|\\|", "Spoiler", RegexOptions.Singleline);
        text = Regex.Replace(text, "(\\*\\*|__|~~|`{1,3})", "");
        text = Regex.Replace(text, "^\\s*>+\\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        if (text.Length == 0) text = Array(d, "attachments").Any() ? "Sent an attachment" : Array(d, "sticker_items").Any() ? "Sent a sticker" : "Sent a message";
        return text.Length > most ? text[..most] + "…" : text;
    }

    /// <summary>
    /// A DM's last message, as its row says it: "You: …" for yours, the author's name first in a group. Someone blocked says
    /// nothing. Publishes the row's new line when it changed.
    /// </summary>
    private void KeepDmLine(Channel channel, JsonElement d, List<(string, object)> events)
    {
        var author = d.TryGetProperty("author", out var a) ? ReadUser(a) : null;
        var id = Str(d, "id");
        if (id is null || (channel.SaidId is { } kept && Snowflake(id) < Snowflake(kept))) return;
        var line = author is null || _blocked.Contains(author.Id) ? null
            : author.Id == Me?.Id ? "You: " + PlainLine(d, 140)
            : channel.Recipients.Count > 1 ? $"{author.Name}: {PlainLine(d, 140)}" : PlainLine(d, 140);
        if (channel.SaidId == id && channel.Said == line) return;
        (channel.Said, channel.SaidId) = (line, id);
        events.Add(("discord.dmLine", new { channelId = channel.Id, line, at = SnowflakeMs(id) }));
    }

    /// <summary>The newest messages of a DM were read (opened, or the pointer rested on it): its row learns what it says. The page's order is Discord's, newest first.</summary>
    internal List<(string, object)> KeepNewest(string channelId, JsonElement page)
    {
        var events = new List<(string, object)>();
        lock (_gate)
            if (_private.TryGetValue(channelId, out var channel) && page.ValueKind == JsonValueKind.Array)
                foreach (var message in page.EnumerateArray()) { KeepDmLine(channel, message, events); break; }
        return events;
    }

    /// <summary>
    /// Every voice room with people in it that you can see, in rooms with your friends first, then your servers in their order.
    /// A server's AFK channel doesn't count, as in Discord's own tooltip. Each room has a few faces (friends first) and its count.
    /// </summary>
    internal List<object> VoiceNow()
    {
        lock (_gate)
        {
            var rooms = new List<(int Friends, bool Muted, int Order, object Room)>();
            var order = 0;
            foreach (var guild in _order.Where(_guilds.ContainsKey).Select(id => _guilds[id]))
            {
                order++;
                if (guild.Voice.Count == 0) continue;
                foreach (var people in guild.Voice.GroupBy(v => v.Value.ChannelId))
                {
                    if (people.Key == guild.AfkChannelId || !guild.Channels.TryGetValue(people.Key, out var c)) continue;
                    var perms = Perms(c);
                    if ((perms & ViewChannel) == 0) continue;
                    var friends = people.Count(v => _relationships.GetValueOrDefault(v.Key) == 1);
                    var muted = IsMuted(guild.Id) || MutedChannel(c);
                    rooms.Add((friends, muted, order, new
                    {
                        channelId = c.Id,
                        name = c.Name ?? "",
                        guildId = guild.Id,
                        guild = guild.Name,
                        icon = guild.Icon is null ? null : $"https://cdn.discordapp.com/icons/{guild.Id}/{guild.Icon}.webp?size=256&quality=lossless",
                        muted,
                        canConnect = (perms & Connect) != 0,
                        limit = c.UserLimit > 0 ? c.UserLimit : (int?)null,
                        count = people.Count(),
                        friends,
                        streaming = people.Any(v => v.Value.Stream),
                        people = people.OrderByDescending(v => _relationships.GetValueOrDefault(v.Key) == 1).Take(MostFaces).Select(v => new
                        {
                            id = v.Key,
                            name = guild.Members.TryGetValue(v.Key, out var known) && known.Nick is { } nick ? nick : _users.GetValueOrDefault(v.Key)?.Name ?? v.Value.Name,
                            avatar = Avatar(v.Key),
                        }).ToArray(),
                    }));
                }
            }
            return [.. rooms.OrderByDescending(r => r.Friends).ThenBy(r => r.Muted).ThenBy(r => r.Order).Take(MostRooms).Select(r => r.Room)];
        }
    }

    private static long SnowflakeMs(string id) => (long)(Snowflake(id) >> 22) + 1420070400000L;
}
