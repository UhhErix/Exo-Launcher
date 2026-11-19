using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using ExoLauncher.Helpers;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Discord's soundboard in the call's own sound: a sound pressed here, or one someone else in the call played, is fetched
/// from Discord's CDN (cdn.discordapp.com/soundboard-sounds/{id}, MP3 or Ogg as Discord's docs say), decoded to 48 kHz
/// stereo off the audio thread, and mixed into the call's speakers (<see cref="VoiceAudio.PlaySound"/>) at its volume. So
/// it plays where the call plays: on the call's speakers at Output Volume, silent while deafened, in the echo canceller's
/// reference (its echo is taken out of the mic) and in <see cref="CallEcho.Heard"/>, and never in a Go Live's sound, which
/// leaves Exo itself out. It played in Exo's page before, and a screen's sound took the page for the Music room, sounds
/// and all. MP3 (Discord's own sounds are) through Media Foundation, on every Windows; Ogg Opus (what Discord's own
/// client expects an Ogg sound to be) through Exo's libopus (<see cref="OggOpus.Read"/>), never Windows' optional media
/// extensions; anything else through Windows' decoders where it has one, and app.log says when it hasn't. The last
/// <see cref="Kept"/> sounds' files stay in memory (Discord's limit is 512 KB a file), none on disk.
/// </summary>
internal static class Soundboard
{
    private const int MostBytes = 1 << 20, Kept = 16, MostSeconds = 10;
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(10));
    private static readonly LinkedList<(string Id, byte[] File)> Files = []; // the last played first
    private static readonly Lock Gate = new();

    /// <summary>Plays a sound into the call's mix at <paramref name="volume"/> (0 to 1), fetched and decoded off the caller's thread.</summary>
    internal static void Play(VoiceAudio audio, string soundId, float volume)
    {
        if (!IsId(soundId) || !(volume > 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await DecodeAsync(soundId).ConfigureAwait(false) is { } sound) audio.PlaySound(sound, Math.Min(volume, 1f));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Info("Discord voice: a soundboard sound didn't play (" + ex.GetType().Name + ").");
            }
        });
    }

    /// <summary>A sound id: Discord's own are small numbers, a server's a snowflake.</summary>
    internal static bool IsId(string? id) => id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit);

    /// <summary>The sound as 48 kHz interleaved stereo; null when it can't be fetched or decoded.</summary>
    internal static async Task<float[]?> DecodeAsync(string soundId)
    {
        if (await FileAsync(soundId).ConfigureAwait(false) is not { } file) return null;
        var sound = await DecodeAsync(file).ConfigureAwait(false);
        if (sound is null) AppLog.Info($"Discord voice: a soundboard sound couldn't be decoded ({Kind(file)}).");
        return sound;
    }

    /// <summary>A sound file as 48 kHz interleaved stereo, at most <see cref="MostSeconds"/> long; null when it can't be decoded.</summary>
    internal static async Task<float[]?> DecodeAsync(byte[] file) =>
        OggOpus.IsOpus(file) ? OggOpus.Read(file, MostSeconds * Opus.SampleRate) : await WindowsDecodeAsync(file).ConfigureAwait(false);

    private static string Kind(ReadOnlySpan<byte> file) =>
        file.Length >= 4 && file[..4].SequenceEqual("OggS"u8) ? (file.IndexOf("\u0001vorbis"u8) is >= 0 and < 128 ? "Ogg Vorbis" : "Ogg")
        : file.Length >= 3 && (file[..3].SequenceEqual("ID3"u8) || file[0] == 0xFF && (file[1] & 0xE0) == 0xE0) ? "MP3" : "unknown";

    private static async Task<byte[]?> FileAsync(string soundId)
    {
        lock (Gate)
            for (var node = Files.First; node is not null; node = node.Next)
                if (node.Value.Id == soundId)
                {
                    Files.Remove(node);
                    Files.AddFirst(node);
                    return node.Value.File;
                }
        using var response = await Http.GetAsync($"https://cdn.discordapp.com/soundboard-sounds/{soundId}", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MostBytes) return null;
        var file = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (file.Length is 0 or > MostBytes) return null;
        lock (Gate)
        {
            Files.AddFirst((soundId, file));
            while (Files.Count > Kept) Files.RemoveLast();
        }
        return file;
    }

    // Media Foundation through Windows' transcoder: the file to a 48 kHz stereo WAV in memory, resampled as needed.
    private static async Task<float[]?> WindowsDecodeAsync(byte[] file)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(file.AsBuffer());
        input.Seek(0);
        using var output = new InMemoryRandomAccessStream();
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(Opus.SampleRate, Opus.Channels, 16);
        var prepared = await new MediaTranscoder().PrepareStreamTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode) return null;
        await prepared.TranscodeAsync();
        if (output.Size is 0 or > (MostSeconds + 1) * Opus.SampleRate * Opus.Channels * 2 + 4096) return null;
        var wav = new byte[output.Size];
        await output.GetInputStreamAt(0).ReadAsync(wav.AsBuffer(), (uint)wav.Length, InputStreamOptions.None);
        return Pcm16(wav);
    }

    /// <summary>A 16-bit stereo WAV's samples as floats (its data chunk, at most <see cref="MostSeconds"/>); null when it has none.</summary>
    internal static float[]? Pcm16(ReadOnlySpan<byte> wav)
    {
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]), (uint)(wav.Length - at - 8));
            if (wav.Slice(at, 4).SequenceEqual("data"u8))
            {
                var samples = Math.Min(size / 2, MostSeconds * Opus.SampleRate * Opus.Channels) & ~1;
                if (samples == 0) return null;
                var pcm = new float[samples];
                for (var i = 0; i < samples; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(wav[(at + 8 + 2 * i)..]) / 32768f;
                return pcm;
            }
            at += 8 + size + (size & 1);
        }
        return null;
    }
}
