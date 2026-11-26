using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

internal enum VoiceModelTier
{
    FallbackDfn3,
    DpdfNetBaseline,
    DpdfNet48kHr,
    VoxLockTse,
}

internal static class VoiceModels
{
    internal static readonly GitHubReleaseAsset DpdfNetBaselineAsset = new(
        "UhhErix", "Exo-Launcher", "voice-models-v2", "dpdfnet_baseline.onnx",
        8_791_035, "debaf0e8893479fca91b8b4c1eae8db195aa8980ccc9012f5809fde2b738151a");

    internal static readonly GitHubReleaseAsset DpdfNet48kHrAsset = new(
        "UhhErix", "Exo-Launcher", "voice-models-v2", "dpdfnet2_48khz_hr.onnx",
        10_596_848, "0b399f8a58dc4d70d8cd97541f5c39869406145193b957d00a03b66070944928");

    internal static readonly GitHubReleaseAsset TitaNetAsset = new(
        "UhhErix", "Exo-Launcher", "voice-models-v2", "nemo_en_titanet_small.onnx",
        40_257_283, "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e");

    internal static IReadOnlyList<GitHubReleaseAsset> Bundled { get; } =
        [DpdfNetBaselineAsset, DpdfNet48kHrAsset, TitaNetAsset];

    internal static readonly string[] RetiredModelDownloads =
    [
        "titanet_small.onnx",
        "exo_dpdfnet_baseline_gaming.onnx",
        "exo_dpdfnet2_48khz_hr_gaming.onnx",
        "voxlock_enroll.onnx",
        "voxlock_chunk_int8.onnx",
    ];

    internal static string VoiceDir => Path.Combine(PathHelper.AppDataDir, "voice");

    internal static string BundledPath(GitHubReleaseAsset asset) =>
        Path.Combine(AppContext.BaseDirectory, "voice", "models", asset.AssetName);

    internal static string ModelPath(GitHubReleaseAsset asset)
    {
        var bundled = BundledPath(asset);
#if DEBUG
        var downloaded = Path.Combine(VoiceDir, asset.AssetName);
        if (!File.Exists(bundled) && File.Exists(downloaded)) return downloaded;
#endif
        return bundled;
    }

    internal static IEnumerable<string> RetiredDownloads()
    {
#if DEBUG
        return [];
#else
        return [.. Bundled.Select(asset => Path.Combine(VoiceDir, asset.AssetName)), .. RetiredModelDownloads.Select(name => Path.Combine(VoiceDir, name))];
#endif
    }

    internal static string BaselineModelPath { get; set; } = ModelPath(DpdfNetBaselineAsset);
    internal static string HighQualityModelPath { get; set; } = ModelPath(DpdfNet48kHrAsset);
    internal static string EnrollmentAudioPath { get; set; } = Path.Combine(VoiceDir, "my-voice-enroll.bin");

    private static (long Size, DateTime Written, bool Whole)? _baselineChecked;
    private static (long Size, DateTime Written, bool Whole)? _hqChecked;

    private static bool CheckPinned(GitHubReleaseAsset asset, string path, ref (long Size, DateTime Written, bool Whole)? cache)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return false;
        if (cache is { } last && last.Size == file.Length && last.Written == file.LastWriteTimeUtc) return last.Whole;
        var whole = VerifiedGitHubReleaseDownloader.IsPinnedAssetFile(asset, path, _ => true);
        cache = (file.Length, file.LastWriteTimeUtc, whole);
        return whole;
    }

    internal static bool BaselineReady() => CheckPinned(DpdfNetBaselineAsset, BaselineModelPath, ref _baselineChecked);
    internal static bool HighQualityReady() => CheckPinned(DpdfNet48kHrAsset, HighQualityModelPath, ref _hqChecked);
    internal static bool VoxLockReady() => false;

    internal static void SaveEnrollmentAudio(ReadOnlySpan<float> voice16k)
    {
        try
        {
            const int maxSamples = 16000 * 3;
            var slice = voice16k.Length > maxSamples ? voice16k[..maxSamples] : voice16k;
            var raw = new byte[slice.Length * sizeof(float)];
            Buffer.BlockCopy(slice.ToArray(), 0, raw, 0, raw.Length);
            var sealedBytes = ExoDpapi.Protect(raw);
            Directory.CreateDirectory(Path.GetDirectoryName(EnrollmentAudioPath)!);
            File.WriteAllBytes(EnrollmentAudioPath, sealedBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            AppLog.Info("Discord voice: couldn't save voice isolation sample (" + ex.GetType().Name + ").");
        }
    }

    internal static float[]? LoadEnrollmentAudio()
    {
        try
        {
            if (!File.Exists(EnrollmentAudioPath)) return null;
            var raw = ExoDpapi.Unprotect(File.ReadAllBytes(EnrollmentAudioPath));
            if (raw.Length < 8000 * sizeof(float) || raw.Length % sizeof(float) != 0) return null;
            var floats = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, floats, 0, raw.Length);
            return floats;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    internal static void ForgetEnrollmentAudio()
    {
        try
        {
            if (File.Exists(EnrollmentAudioPath)) DeletionGate.DeleteFile(EnrollmentAudioPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class VoiceTierPolicy
{
    internal const double MaxCpuRtf = 0.20;
    internal const double MinGameGpuHeadroomMs = 0.30;
    internal const int HighEndCpuCoreThreshold = 12;

    internal static string NormalizeTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "cpu" => "cpu",
        "hq" or "studio" or "48k" => "hq",
        _ => "auto",
    };

    internal static bool MeetsCpuBudget(double msPer10MsFrame) => msPer10MsFrame / 10.0 <= MaxCpuRtf;

    internal static bool ShouldUseHighQuality(
        string tier,
        bool hqReady,
        bool gameInForeground,
        double gpuHeadroomMs,
        int cpuLogicalCores)
    {
        if (!hqReady) return false;
        var norm = NormalizeTier(tier);
        if (norm == "cpu") return false;
        if (gameInForeground && gpuHeadroomMs < MinGameGpuHeadroomMs) return false;
        if (norm == "hq") return true;
        return !gameInForeground && (gpuHeadroomMs >= MinGameGpuHeadroomMs || cpuLogicalCores >= HighEndCpuCoreThreshold);
    }

    internal static VoiceModelTier SelectTier(
        string suppressorKind,
        string voiceTier,
        bool baselineReady,
        bool hqReady,
        bool voxLockReady,
        bool onlyMyVoiceWithIsolation,
        bool gameInForeground,
        double gpuHeadroomMs,
        int cpuLogicalCores)
    {
        if (suppressorKind == "exo-dfn3") return VoiceModelTier.FallbackDfn3;
        if (onlyMyVoiceWithIsolation && voxLockReady) return VoiceModelTier.VoxLockTse;
        if (ShouldUseHighQuality(voiceTier, hqReady, gameInForeground, gpuHeadroomMs, cpuLogicalCores))
            return VoiceModelTier.DpdfNet48kHr;
        if (baselineReady) return VoiceModelTier.DpdfNetBaseline;
        return VoiceModelTier.FallbackDfn3;
    }
}
