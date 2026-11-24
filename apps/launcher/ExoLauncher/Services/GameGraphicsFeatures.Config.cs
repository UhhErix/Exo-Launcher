using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Services;

internal static partial class GameGraphicsFeatures
{
    private const string IniBegin = "; Exo Graphics Features begin";
    private const string IniEnd = "; Exo Graphics Features end";

    private static string? RestoreAllTrackedFiles(string stateRoot, GameRecord record)
    {
        var backupRoot = BackupsDir(stateRoot);
        foreach (var entry in record.FileBackups.Values.ToList())
        {
            try
            {
                if (!entry.ExistedBefore)
                {
                    if (File.Exists(entry.OriginalPath)) DeletionGate.DeleteFile(entry.OriginalPath);
                }
                else
                {
                    var backupFile = Path.Combine(backupRoot, entry.BackupRelativePath);
                    if (!File.Exists(backupFile)) continue;
                    var originalBytes = File.ReadAllBytes(backupFile);
                    var dir = Path.GetDirectoryName(entry.OriginalPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    WriteBytesAtomic(entry.OriginalPath, originalBytes);
                    TryDeleteSqliteSidecars(entry.OriginalPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not restore {Path.GetFileName(entry.OriginalPath)}: {ex.Message}";
            }
        }

        foreach (var entry in record.FileBackups.Values)
        {
            try
            {
                var backupFile = Path.Combine(backupRoot, entry.BackupRelativePath);
                if (File.Exists(backupFile)) DeletionGate.DeleteFile(backupFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        record.FileBackups.Clear();
        return null;
    }

    private static void EnsureBackup(string stateRoot, GameRecord record, string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        if (record.FileBackups.ContainsKey(full)) return;
        var backupRoot = BackupsDir(stateRoot);
        Directory.CreateDirectory(backupRoot);

        if (File.Exists(full))
        {
            var raw = File.ReadAllBytes(full);
            var hash = Sha256Hex(raw);
            var keyHash = Sha256Hex(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))[..16];
            var rel = $"{keyHash}-{hash[..12]}.orig";
            var dest = Path.Combine(backupRoot, rel);
            WriteBytesAtomic(dest, raw);
            record.FileBackups[full] = new FileBackupRecord(full, rel, true, hash);
        }
        else
        {
            var keyHash = Sha256Hex(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))[..16];
            record.FileBackups[full] = new FileBackupRecord(full, $"{keyHash}.absent", false, null);
        }
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".exo-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDeleteSqliteSidecars(string dbPath)
    {
        if (!dbPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var suffix in (string[])["-wal", "-shm"])
        {
            try
            {
                var sidecar = dbPath + suffix;
                if (File.Exists(sidecar)) DeletionGate.DeleteFile(sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? ApplyTargetConfigs(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> targetFeatures)
    {
        try
        {
            if (d.HasOptiScaler && AllowsDllMod(game, ctx.AntiCheat) && d.ProgramFolder is not null && (targetFeatures.Contains(FeatureUpscalers) || targetFeatures.Contains(FeatureReflex)))
            {
                var optiIni = Path.Combine(d.ProgramFolder, "OptiScaler.ini");
                EnsureBackup(ctx.StateRoot, record, optiIni);
                ApplyOptiScalerConfig(optiIni, ctx.Pc, targetFeatures.Contains(FeatureUpscalers), targetFeatures.Contains(FeatureReflex));
            }

            switch (d.Engine)
            {
                case EngineKind.WutheringWaves:
                    ApplyWutheringWavesConfigs(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.Northlight:
                    ApplyNorthlightConfig(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.RedEngine:
                    ApplyCyberpunkConfig(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.Unreal5:
                case EngineKind.Unreal4:
                    ApplyUnrealConfigs(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.FromSoftware:
                    ApplySekiroConfig(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.ReEngine:
                    ApplyReEngineConfig(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.Frostbite:
                    ApplyFrostbiteConfig(d, ctx, record, targetFeatures);
                    break;
                case EngineKind.IdTech:
                    ApplyIdTechConfig(d, ctx, record, targetFeatures);
                    break;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return $"Could not save config for {game.Title}: {ex.Message}";
        }
    }

    private static void ApplyWutheringWavesConfigs(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        var dbPairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var gusPairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var engineVars = new List<(string Key, string Value)>();

        if (features.Contains(FeatureRayTracing))
        {
            dbPairs["RayTracing"] = "3";
            dbPairs["RayTracedGI"] = "1";
            dbPairs["RayTracedReflection"] = "1";
            dbPairs["RayTracedShadow"] = "1";
            gusPairs["sg.RayTracingQuality"] = "3";
            engineVars.Add(("r.RayTracing", "1"));
            engineVars.Add(("r.RayTracing.EnableInGame", "1"));
        }

        if (features.Contains(FeatureHiddenSettings))
        {
            dbPairs["CustomFrameRate"] = "3";
            dbPairs["AnisoLevel"] = "4";
            dbPairs["ImageDetail"] = "3";
            gusPairs["FrameRateLimit"] = "120.000000";
            gusPairs["FramePace"] = "0";
            gusPairs["sg.ResolutionQuality"] = "100.000000";
            engineVars.Add(("r.MaxAnisotropy", "16"));
            engineVars.Add(("r.MipMapLODBias", "-0.5"));
            engineVars.Add(("r.Streaming.FullyLoadUsedTextures", "1"));
        }

        if (features.Contains(FeatureUpscalers))
        {
            dbPairs["NvidiaSuperSamplingEnable"] = "1";
            dbPairs["NvidiaSuperSamplingQuality"] = "3";
        }

        if (features.Contains(FeatureReflex))
        {
            engineVars.Add(("t.Streamline.Reflex.Mode", "1"));
        }

        if (dbPairs.Count > 0 && d.WuWaLocalStoragePath is not null && File.Exists(d.WuWaLocalStoragePath))
        {
            EnsureBackup(ctx.StateRoot, record, d.WuWaLocalStoragePath);
            WriteWuWaLocalStorage(d.WuWaLocalStoragePath, dbPairs);
        }

        if (gusPairs.Count > 0 && d.UnrealGameUserSettingsPath is not null && File.Exists(d.UnrealGameUserSettingsPath))
        {
            EnsureBackup(ctx.StateRoot, record, d.UnrealGameUserSettingsPath);
            PatchIniKeysInPlace(d.UnrealGameUserSettingsPath, gusPairs);
        }

        if (engineVars.Count > 0 && d.UnrealEngineIniPath is not null)
        {
            EnsureBackup(ctx.StateRoot, record, d.UnrealEngineIniPath);
            WriteSystemSettingsIniBlock(d.UnrealEngineIniPath, engineVars);
        }
    }

    internal static void WriteWuWaLocalStorage(string dbPath, IReadOnlyDictionary<string, string> updates)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var con = new SqliteConnection(csb.ToString());
        con.Open();
        using var tx = con.BeginTransaction();
        foreach (var (key, value) in updates)
        {
            using var updateCmd = con.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE LocalStorage SET value = $val WHERE key = $key";
            updateCmd.Parameters.AddWithValue("$val", value);
            updateCmd.Parameters.AddWithValue("$key", key);
            var rows = updateCmd.ExecuteNonQuery();
            if (rows == 0)
            {
                using var insertCmd = con.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = "INSERT INTO LocalStorage (key, value) VALUES ($key, $val)";
                insertCmd.Parameters.AddWithValue("$key", key);
                insertCmd.Parameters.AddWithValue("$val", value);
                insertCmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    internal static string? ReadWuWaLocalStorageValue(string? dbPath, string key)
    {
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return null;
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var con = new SqliteConnection(csb.ToString());
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT value FROM LocalStorage WHERE key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar()?.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return null;
        }
    }

    internal static bool IsWuWa120FpsEnabledOnDisk(string? dbPath) =>
        ReadWuWaLocalStorageValue(dbPath, "CustomFrameRate") is "3" or "120";

    internal static bool IsWuWaRtEnabledOnDisk(string? dbPath) =>
        int.TryParse(ReadWuWaLocalStorageValue(dbPath, "RayTracing"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rt) && rt > 0;

    internal static bool IsWuWaDlssEnabledOnDisk(string? dbPath) =>
        ReadWuWaLocalStorageValue(dbPath, "NvidiaSuperSamplingEnable") == "1";

    private static void ApplyNorthlightConfig(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        if (d.NorthlightConfigPath is null || !File.Exists(d.NorthlightConfigPath)) return;
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var isNvidia = string.Equals(ctx.Pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase);

        if (features.Contains(FeatureUpscalers))
        {
            updates["m_eSSAAMethod"] = isNvidia ? "2" : "3";
            if (isNvidia) updates["m_bDLSSLegacy"] = "false";
        }

        if (features.Contains(FeatureRayTracing))
        {
            updates["m_eRTReflectionQuality"] = "2";
            updates["m_eRTTransparentQuality"] = "2";
            updates["m_eRTIndirectDiffuseQuality"] = "1";
            updates["m_eRTReSTIRQuality"] = "1";
            updates["m_eRTDebrisQuality"] = "1";
            if (isNvidia) updates["m_DLSSRR"] = "true";
        }

        if (features.Contains(FeaturePathTracing))
        {
            updates["m_ePTQuality"] = "2";
            if (isNvidia) updates["m_DLSSRR"] = "true";
        }

        if (features.Contains(FeatureHiddenSettings))
        {
            updates["m_maxFrameRate"] = "1000";
            updates["m_bUnlockAspectRatio"] = "true";
            updates["m_eTextureFilteringQuality"] = "2";
            updates["m_eLODDetail"] = "2";
            updates["m_eShadowDetailQuality"] = "2";
            updates["m_eScatteredEntityQuantity"] = "3";
        }

        if (updates.Count == 0) return;
        EnsureBackup(ctx.StateRoot, record, d.NorthlightConfigPath);
        PatchJsonScalarsInPlace(d.NorthlightConfigPath, updates);
    }

    private static void ApplyCyberpunkConfig(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        if (d.CyberpunkConfigPath is null || !File.Exists(d.CyberpunkConfigPath)) return;
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);

        if (features.Contains(FeatureRayTracing))
        {
            updates["RayTracing"] = "true";
            updates["RayTracedReflections"] = "true";
            updates["RayTracedSunShadows"] = "true";
            updates["RayTracedLocalShadows"] = "true";
        }
        if (features.Contains(FeaturePathTracing))
        {
            updates["RayTracing"] = "true";
            updates["RayTracing_PathTracing"] = "true";
            updates["DLSS_RayReconstruction"] = "true";
        }
        if (features.Contains(FeatureHiddenSettings))
        {
            updates["Anisotropy"] = "16";
            updates["MaxDynamicDecals"] = "Ultra";
            updates["CrowdDensity"] = "High";
        }

        if (updates.Count == 0) return;
        EnsureBackup(ctx.StateRoot, record, d.CyberpunkConfigPath);
        PatchCyberpunkSettings(d.CyberpunkConfigPath, updates);
    }

    private static void ApplyUnrealConfigs(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        var gusUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
        var engineVars = new List<(string Key, string Value)>();

        if (features.Contains(FeatureUpscalers))
        {
            gusUpdates["sg.ResolutionQuality"] = "100";
            engineVars.Add(("r.ScreenPercentage", "100"));
        }

        if (features.Contains(FeatureReflex) && d.HasReflex)
        {
            gusUpdates["NvidiaReflex"] = "EnabledPlusBoost";
            engineVars.Add(("t.Streamline.Reflex.Mode", "1"));
            engineVars.Add(("r.NvidiaReflex.Mode", "1"));
        }

        if (features.Contains(FeatureRayTracing) && d.Engine == EngineKind.Unreal5)
        {
            engineVars.Add(("r.RayTracing", "1"));
            engineVars.Add(("r.Lumen.HardwareRayTracing", "1"));
            engineVars.Add(("r.Lumen.Reflections.HardwareRayTracing", "1"));
            engineVars.Add(("r.RayTracing.Shadows", "1"));
            engineVars.Add(("r.RayTracing.Reflections", "1"));
            engineVars.Add(("r.RayTracing.GlobalIllumination", "1"));
        }

        if (features.Contains(FeaturePathTracing) && d.Engine == EngineKind.Unreal5)
        {
            engineVars.Add(("r.RayTracing", "1"));
            engineVars.Add(("r.PathTracing", "1"));
            engineVars.Add(("r.PathTracing.MaxBounces", "4"));
        }

        if (features.Contains(FeatureHiddenSettings))
        {
            gusUpdates["FrameRateLimit"] = "0.000000";
            gusUpdates["bUseVSync"] = "False";
            gusUpdates["sg.ResolutionQuality"] = "100";
            var poolSize = (ctx.Pc.VideoMemoryGb ?? 8.0) >= 12.0 ? "6144" : "4096";
            engineVars.Add(("r.MaxAnisotropy", "16"));
            engineVars.Add(("r.ViewDistanceScale", "1.5"));
            engineVars.Add(("r.Shadow.DistanceScale", "1.5"));
            engineVars.Add(("r.StaticMeshLODDistanceScale", "0.5"));
            engineVars.Add(("r.MipMapLODBias", "-0.5"));
            engineVars.Add(("r.SceneColorFringeQuality", "0"));
            engineVars.Add(("r.Tonemapper.GrainQuantization", "0"));
            engineVars.Add(("r.Streaming.PoolSize", poolSize));
        }

        if (gusUpdates.Count > 0 && d.UnrealGameUserSettingsPath is not null && File.Exists(d.UnrealGameUserSettingsPath))
        {
            EnsureBackup(ctx.StateRoot, record, d.UnrealGameUserSettingsPath);
            PatchIniKeysInPlace(d.UnrealGameUserSettingsPath, gusUpdates);
        }

        if (engineVars.Count > 0 && d.UnrealEngineIniPath is not null)
        {
            var deduped = engineVars
                .GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();
            EnsureBackup(ctx.StateRoot, record, d.UnrealEngineIniPath);
            WriteSystemSettingsIniBlock(d.UnrealEngineIniPath, deduped);
        }
    }

    private static void ApplySekiroConfig(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        if (!features.Contains(FeatureHiddenSettings) || d.SekiroConfigPath is null || !File.Exists(d.SekiroConfigPath))
            return;

        EnsureBackup(ctx.StateRoot, record, d.SekiroConfigPath);
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Auto-detectBestRenderingSettings"] = "OFF",
            ["QualitySetting"] = "CUSTOM",
            ["TextureQuality"] = "MAX",
            ["SSAO"] = "MAX",
            ["ShadowQuality"] = "MAX",
            ["LightingQuality"] = "MAX",
            ["EffectsQuality"] = "MAX",
            ["ReflectionQuality"] = "MAX",
            ["WaterSurfaceQuality"] = "HIGH",
            ["ShadeQuality"] = "HIGH",
            ["LodQuality"] = "HIGH",
            ["VolumetricEffectQuality"] = "MAX",
        };
        PatchXmlTagsPreservingEncoding(d.SekiroConfigPath, updates);
    }

    private static void ApplyReEngineConfig(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        if (!features.Contains(FeatureRayTracing) || d.ReEngineConfigPath is null || !File.Exists(d.ReEngineConfigPath))
            return;
        EnsureBackup(ctx.StateRoot, record, d.ReEngineConfigPath);
        PatchIniKeysInPlace(d.ReEngineConfigPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RayTracing"] = "Enable",
        });
    }

    private static void ApplyFrostbiteConfig(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        if (!features.Contains(FeatureRayTracing) || d.FrostbiteConfigPath is null || !File.Exists(d.FrostbiteConfigPath))
            return;
        EnsureBackup(ctx.StateRoot, record, d.FrostbiteConfigPath);
        var raw = File.ReadAllBytes(d.FrostbiteConfigPath);
        var text = Encoding.UTF8.GetString(raw);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(nl).ToList();
        ReplaceOrAppendSpacePair(lines, "GstRender.Dx12Enabled", "1");
        ReplaceOrAppendSpacePair(lines, "GstRender.RaytracingReflections", "1");
        ReplaceOrAppendSpacePair(lines, "GstRender.RaytracingAmbientOcclusion", "1");
        WriteBytesAtomic(d.FrostbiteConfigPath, Encoding.UTF8.GetBytes(string.Join(nl, lines)));
    }

    private static void ApplyIdTechConfig(
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> features)
    {
        if (d.IdTechConfigPath is null || !File.Exists(d.IdTechConfigPath)) return;
        if (!features.Contains(FeatureRayTracing) && !features.Contains(FeaturePathTracing)) return;
        EnsureBackup(ctx.StateRoot, record, d.IdTechConfigPath);
        var raw = File.ReadAllBytes(d.IdTechConfigPath);
        var text = Encoding.UTF8.GetString(raw);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(nl).ToList();
        if (features.Contains(FeatureRayTracing)) ReplaceOrAppendSpacePair(lines, "r_raytracing", "\"1\"");
        if (features.Contains(FeaturePathTracing)) ReplaceOrAppendSpacePair(lines, "r_pathtracing", "\"1\"");
        WriteBytesAtomic(d.IdTechConfigPath, Encoding.UTF8.GetBytes(string.Join(nl, lines)));
    }

    private static void ReplaceOrAppendSpacePair(List<string> lines, string key, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(key + " ", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key} {value}";
                return;
            }
        }
        lines.Add($"{key} {value}");
    }

    internal static void WriteSystemSettingsIniBlock(string iniPath, IReadOnlyList<(string Key, string Value)> vars)
    {
        var dir = Path.GetDirectoryName(iniPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var raw = File.Exists(iniPath) ? File.ReadAllBytes(iniPath) : [];
        var (bom, enc, nl, lines) = DecodeText(raw);
        var cleaned = StripManagedIniBlock(lines);
        var sectionIdx = cleaned.FindIndex(line => line.Trim().Equals("[SystemSettings]", StringComparison.OrdinalIgnoreCase));

        var block = new List<string> { IniBegin };
        foreach (var (key, value) in vars) block.Add($"{key}={value}");
        block.Add(IniEnd);

        if (sectionIdx >= 0)
        {
            cleaned.InsertRange(sectionIdx + 1, block);
        }
        else
        {
            if (cleaned.Count > 0 && cleaned[^1].Trim().Length > 0) cleaned.Add("");
            cleaned.Add("[SystemSettings]");
            cleaned.AddRange(block);
        }

        WriteBytesAtomic(iniPath, EncodeText(bom, enc, nl, cleaned));
    }

    private static List<string> StripManagedIniBlock(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var skipping = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(IniBegin, StringComparison.Ordinal)) { skipping = true; continue; }
            if (trimmed.Equals(IniEnd, StringComparison.Ordinal)) { skipping = false; continue; }
            if (!skipping) result.Add(line);
        }
        return result;
    }

    internal static void PatchIniKeysInPlace(string iniPath, IReadOnlyDictionary<string, string> updates)
    {
        var raw = File.ReadAllBytes(iniPath);
        var (bom, enc, nl, lines) = DecodeText(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (updates.TryGetValue(key, out var nextValue))
            {
                var prefix = line[..(eq + 1)];
                lines[i] = prefix + nextValue;
            }
        }
        WriteBytesAtomic(iniPath, EncodeText(bom, enc, nl, lines));
    }

    internal static void PatchJsonScalarsInPlace(string jsonPath, IReadOnlyDictionary<string, string> updates)
    {
        var raw = File.ReadAllBytes(jsonPath);
        var (bom, enc, nl, lines) = DecodeText(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            var match = JsonScalarRegex().Match(lines[i]);
            if (!match.Success) continue;
            var key = match.Groups["name"].Value;
            if (!updates.TryGetValue(key, out var nextVal)) continue;
            var valGroup = match.Groups["value"];
            lines[i] = string.Concat(lines[i].AsSpan(0, valGroup.Index), nextVal, lines[i].AsSpan(valGroup.Index + valGroup.Length));
        }
        WriteBytesAtomic(jsonPath, EncodeText(bom, enc, nl, lines));
    }

    private static void PatchCyberpunkSettings(string jsonPath, IReadOnlyDictionary<string, string> updates)
    {
        var raw = File.ReadAllBytes(jsonPath);
        var (bom, enc, nl, lines) = DecodeText(raw);
        var activeName = "";
        for (var i = 0; i < lines.Count; i++)
        {
            var m = CyberpunkFieldRegex().Match(lines[i]);
            if (!m.Success) continue;
            var field = m.Groups["field"].Value;
            if (field == "name") { activeName = m.Groups["value"].Value; continue; }
            if (field == "value" && updates.TryGetValue(activeName, out var nextVal))
            {
                var g = m.Groups["value"];
                lines[i] = string.Concat(lines[i].AsSpan(0, g.Index), nextVal, lines[i].AsSpan(g.Index + g.Length));
            }
        }
        WriteBytesAtomic(jsonPath, EncodeText(bom, enc, nl, lines));
    }

    internal static void PatchXmlTagsPreservingEncoding(string xmlPath, IReadOnlyDictionary<string, string> updates)
    {
        var raw = File.ReadAllBytes(xmlPath);
        var (bom, enc, nl, lines) = DecodeText(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = XmlTagRegex().Replace(lines[i], match =>
            {
                var tag = match.Groups["tag"].Value;
                return updates.TryGetValue(tag, out var nextVal) ? $"<{tag}>{nextVal}</{tag}>" : match.Value;
            });
        }
        WriteBytesAtomic(xmlPath, EncodeText(bom, enc, nl, lines));
    }

    private static (byte[] Bom, Encoding Encoding, string NewLine, List<string> Lines) DecodeText(byte[] raw)
    {
        if (raw is [0xFF, 0xFE, ..])
        {
            var text = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return ([0xFF, 0xFE], new UnicodeEncoding(false, false), nl, [.. text.Split(nl)]);
        }
        if (raw.Length >= 4 && raw[0] != 0 && raw[1] == 0 && raw[2] != 0 && raw[3] == 0)
        {
            var text = Encoding.Unicode.GetString(raw);
            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return ([], new UnicodeEncoding(false, false), nl, [.. text.Split(nl)]);
        }
        if (raw is [0xFE, 0xFF, ..])
        {
            var text = Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return ([0xFE, 0xFF], new UnicodeEncoding(true, false), nl, [.. text.Split(nl)]);
        }
        if (raw is [0xEF, 0xBB, 0xBF, ..])
        {
            var text = Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return ([0xEF, 0xBB, 0xBF], new UTF8Encoding(false), nl, [.. text.Split(nl)]);
        }
        var plain = raw.Length == 0 ? "" : Encoding.Latin1.GetString(raw);
        var eol = plain.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return ([], Encoding.Latin1, eol, raw.Length == 0 ? [] : [.. plain.Split(eol)]);
    }

    private static byte[] EncodeText(byte[] bom, Encoding enc, string nl, List<string> lines)
    {
        var body = enc.GetBytes(string.Join(nl, lines));
        if (bom.Length == 0) return body;
        var combined = new byte[bom.Length + body.Length];
        bom.CopyTo(combined, 0);
        body.CopyTo(combined, bom.Length);
        return combined;
    }

    [GeneratedRegex("\"(?<name>[A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*(?<value>true|false|-?\\d+(?:\\.\\d+)?)")]
    private static partial Regex JsonScalarRegex();

    [GeneratedRegex(@"^\s*""(?<field>name|value)""\s*:\s*(?<quote>""?)(?<value>[^"",]*)\k<quote>\s*,?\s*$")]
    private static partial Regex CyberpunkFieldRegex();

    [GeneratedRegex(@"<(?<tag>[A-Za-z0-9_-]+)>(?<value>[^<]*)</\k<tag>>")]
    private static partial Regex XmlTagRegex();
}
