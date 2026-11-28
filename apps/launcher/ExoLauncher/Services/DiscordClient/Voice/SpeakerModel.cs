using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// NVIDIA's TitaNet-small speaker embedding (NeMo, CC-BY-4.0), as sherpa-onnx publishes it in ONNX, for
/// <see cref="MyVoice"/>: it ships beside Exo in voice\models (<see cref="VoiceModels.TitaNetAsset"/>, pinned size and
/// SHA-256; a missing or changed file is a broken install) and runs through exo_voice_ml.dll with onnxruntime.dll.
/// One extractor serves every chain, one embedding at a time; each takes about 30 ms of one core for 1.5 s of voice,
/// off the audio thread.
/// </summary>
internal static class SpeakerModel
{
    internal static string ModelPath { get; set; } = VoiceModels.ModelPath(VoiceModels.TitaNetAsset);

    private static (long Size, DateTime Written, bool Whole)? _checked;

    /// <summary>Whether the model is here and whole; hashes it only when the file changed since it last checked.</summary>
    internal static bool Ready()
    {
        var file = new FileInfo(ModelPath);
        if (!file.Exists) return false;
        if (_checked is { } last && last.Size == file.Length && last.Written == file.LastWriteTimeUtc) return last.Whole;
        var whole = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(VoiceModels.TitaNetAsset, ModelPath, _ => true);
        _checked = (file.Length, file.LastWriteTimeUtc, whole);
        return whole;
    }

    private static readonly Lock Gate = new();

    /// <summary>
    /// Loads the model ahead of a call when there is a voice to judge (a voiceprint taught): as Discord connects
    /// (DiscordSession.Start), so Only My Voice holds back other voices from a call's first moment, not from its first
    /// judgment. Never on the audio thread.
    /// </summary>
    internal static void Warm()
    {
        if (!Voiceprint.Learned) return;
        lock (Gate) Load();
    }

    internal static int SessionThreads(int processorCount) => Math.Clamp(processorCount, 1, 2);

    private static bool Load() => Ready() && ExoVoiceMl.WarmTitaNet(ModelPath);

    /// <summary>The voiceprint of 16 kHz mono voice, unit length; null when the model isn't here or can't judge it.</summary>
    internal static float[]? Embed(ReadOnlySpan<float> voice16k)
    {
        lock (Gate)
        {
            try
            {
                if (!Ready()) return null;
                return ExoVoiceMl.EmbedTitaNet(voice16k, ModelPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                AppLog.Info("Discord voice: the voice model is unavailable (" + ex.GetType().Name + ").");
                return null;
            }
        }
    }

}
