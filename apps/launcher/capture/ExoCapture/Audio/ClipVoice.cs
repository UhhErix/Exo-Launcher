using ExoCapture.Edit;
using ExoCapture.Encode;
using ExoCapture.Media;

namespace ExoCapture.Audio;

/// <summary>
/// A saved clip's microphone, cleaned by Exo rather than all the while it waited in the replay (clip.voice): the
/// clip's raw microphone is handed over as 16-bit stereo PCM, Exo writes the cleaned voice back beside it, and the
/// clip's microphone track and mix are made again from it. Whatever goes wrong, the clip keeps its raw microphone.
/// </summary>
internal static class ClipVoice
{
    private const int MicTrack = 1 + Tracks.Mic, MixTrack = 1 + Tracks.Mix, GameTrack = 1 + Tracks.Game, VoiceTrack = 1 + Tracks.Voice;

    private static (string Raw, string Clean) Files(string clip, string folder)
    {
        var name = Controller.CaptureId(Path.GetFullPath(clip));
        return (Path.Combine(folder, name + " raw.exo-replay"), Path.Combine(folder, name + " clean.exo-replay"));
    }

    /// <summary>Writes the clip's microphone as PCM for Exo to clean; false when the clip has no microphone track.</summary>
    internal static bool Export(string clip, string folder, out string raw, out string clean)
    {
        (raw, clean) = Files(clip, folder);
        try
        {
            var mp4 = Mp4File.Read(clip);
            if (mp4.Tracks.Count <= MicTrack || mp4.Tracks[MicTrack].Info.Name != Tracks.Names[Tracks.Mic]) return false;
            Directory.CreateDirectory(folder);
            var pcm = Decode(clip, mp4.Tracks[MicTrack]);
            File.WriteAllBytes(raw, pcm);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CaptureException or SharpGen.Runtime.SharpGenException)
        {
            Host.Log("Clip voice: the microphone stays raw in this clip: " + ex.Message);
            return false;
        }
    }

    /// <summary>A sound track's AAC decoded to one run of 16-bit stereo PCM from the track's start.</summary>
    internal static byte[] Decode(string path, Mp4Track track)
    {
        using var decoder = new AacCodec(encoder: false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var pcm = new MemoryStream();
        foreach (var s in track.Samples)
            foreach (var chunk in decoder.Process(Mp4File.ReadSample(file, s))) pcm.Write(chunk);
        foreach (var chunk in decoder.Flush()) pcm.Write(chunk);
        return pcm.ToArray();
    }

    /// <summary>The clip made again with the cleaned microphone: its own track, and the mix of the game, the call (if kept) and the voice.</summary>
    internal static void Remix(string clip, string folder, bool callVoices)
    {
        var (_, cleanPath) = Files(clip, folder);
        var clean = File.ReadAllBytes(cleanPath);
        var mp4 = Mp4File.Read(clip);
        if (mp4.Tracks.Count <= MicTrack) return;
        var game = Decode(clip, mp4.Tracks[GameTrack]);
        var voice = callVoices ? Decode(clip, mp4.Tracks[VoiceTrack]) : [];
        var frames = mp4.Tracks[MicTrack].Samples.Count;
        var length = frames * AacCodec.FrameSamples * 4;
        var mix = new byte[length];
        for (var i = 0; i + 1 < length; i += 2)
        {
            var sum = Sample(game, i) + Sample(voice, i) + Sample(clean, i);
            var v = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            mix[i] = (byte)v;
            mix[i + 1] = (byte)(v >> 8);
        }
        var micAac = Encode(clean, frames);
        var mixAac = Encode(mix, frames);
        var temp = clip + ".part";
        var writer = new Mp4Writer(temp, [.. mp4.Tracks.Select(t => t.Info)], fragmented: false);
        try
        {
            using var file = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = mp4.Video!.Ticks(mp4.Video.Samples[0]);
            for (var t = 0; t < mp4.Tracks.Count; t++)
            {
                var track = mp4.Tracks[t];
                for (var i = 0; i < track.Samples.Count; i++)
                {
                    var s = track.Samples[i];
                    var data = t == MicTrack && i < micAac.Count ? micAac[i] : t == MixTrack && i < mixAac.Count ? mixAac[i] : Mp4File.ReadSample(file, s);
                    writer.Write(t, data, track.Ticks(s) - start, s.Key);
                }
            }
            writer.Finish();
        }
        finally { writer.Dispose(); }
        File.Move(temp, clip, overwrite: true);
    }

    private static int Sample(byte[] pcm, int at) => at + 1 < pcm.Length ? (short)(pcm[at] | pcm[at + 1] << 8) : 0;

    private static List<byte[]> Encode(byte[] pcm, int frames)
    {
        using var encoder = new AacCodec(encoder: true);
        var output = new List<byte[]>(frames);
        var frameBytes = AacCodec.FrameSamples * 4;
        var buffer = new byte[frameBytes];
        for (var f = 0; output.Count < frames && f < frames + 4; f++)
        {
            Array.Clear(buffer);
            var at = f * frameBytes;
            if (at < pcm.Length) Array.Copy(pcm, at, buffer, 0, Math.Min(frameBytes, pcm.Length - at));
            output.AddRange(encoder.Process(buffer));
        }
        return output;
    }

    internal static void Clean(string clip, string folder)
    {
        var (raw, clean) = Files(clip, folder);
        Clips.TryDelete(raw);
        Clips.TryDelete(clean);
    }
}
