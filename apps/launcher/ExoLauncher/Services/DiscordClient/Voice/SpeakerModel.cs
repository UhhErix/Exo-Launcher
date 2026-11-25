using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// NVIDIA's TitaNet-small speaker embedding (NeMo, CC-BY-4.0), as sherpa-onnx publishes it in ONNX, for
/// <see cref="MyVoice"/>: fetched once from sherpa-onnx's release (pinned size and SHA-256) and run through
/// sherpa-onnx's C interface (sherpa-onnx-c-api.dll and onnxruntime.dll beside Exo, from the
/// org.k2fsa.sherpa.onnx.runtime.win-x64 package). One extractor serves every chain, one embedding at a time;
/// each takes about 30 ms of one core for 1.5 s of voice, off the audio thread.
/// </summary>
internal static class SpeakerModel
{
    internal static readonly GitHubReleaseAsset Model = new(
        "k2-fsa", "sherpa-onnx", "speaker-recongition-models", "nemo_en_titanet_small.onnx",
        40257283, "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e");

    internal static string ModelPath { get; set; } = Path.Combine(PathHelper.AppDataDir, "voice", "titanet_small.onnx");

    /// <summary>TitaNet as one of Exo's parts (<see cref="ExoComponents"/>): setup fetches it, so a teach never waits on it.</summary>
    internal static readonly ExoComponent Component = new("TitaNet voice model", "Only My Voice", Model.ExpectedSize, 0, Ready,
        (progress, ct) => VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(Model, ModelPath, _ => true, ct, done => progress(done, unpacking: false)),
        () => ModelPath);

    private static (long Size, DateTime Written, bool Whole)? _checked;

    /// <summary>Whether the model is here and whole; hashes it only when the file changed since it last checked.</summary>
    internal static bool Ready()
    {
        var file = new FileInfo(ModelPath);
        if (!file.Exists) return false;
        if (_checked is { } last && last.Size == file.Length && last.Written == file.LastWriteTimeUtc) return last.Whole;
        var whole = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(Model, ModelPath, _ => true);
        _checked = (file.Length, file.LastWriteTimeUtc, whole);
        return whole;
    }

    /// <summary>Fetches the model if it isn't here (verified before it is kept); whether it is here now.</summary>
    internal static async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (Ready()) return true;
        try
        {
            await VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(Model, ModelPath, _ => true, ct).ConfigureAwait(false);
            return Ready();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info("Discord voice: the voice model wasn't fetched (" + ex.GetType().Name + ").");
            return false;
        }
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
