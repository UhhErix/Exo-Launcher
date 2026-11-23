using System.Buffers.Binary;
using System.IO;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class WootingKeyboardTests
{
    private static readonly byte[] Real60HeV2ProfileBody = Convert.FromHexString("0A140880E0011001180120E684012801300238014801");
    private static readonly byte[] Real60HeV2RgbBody = Convert.FromHexString("0A0B08B30110FFFF0318FFFF032200");
    private static readonly string[] ValidFormFactors = ["60", "80", "tkl", "full", "keypad3"];

    [Fact]
    public void Catalog_MapsEveryWootingVidPidAndGamepadVariantToValidModelAndFormFactor()
    {
        int[] base31E3Pids =
        [
            0x1100, 0x1200, 0x1210, 0x1220, 0x1230,
            0x1300, 0x1310, 0x1320, 0x1340,
            0x1400, 0x1410, 0x1500, 0x1510,
        ];

        foreach (var basePid in base31E3Pids)
        {
            for (var variant = 0; variant <= 2; variant++)
            {
                var pid = basePid + variant;
                Assert.True(WootingKeyboard.IsWootingIds(WootingKeyboard.VendorId, pid));
                var model = WootingKeyboard.ModelOf(WootingKeyboard.VendorId, pid);
                Assert.NotNull(model);
                Assert.Contains(model.FormFactor, ValidFormFactors);
                Assert.False(model.LegacyV1);
                Assert.True(model.MinActuationMm < model.MaxActuationMm);
            }
        }

        int[] legacyPids = [0xFF01, 0x2402, 0xFF02, 0x2403];
        foreach (var pid in legacyPids)
        {
            Assert.True(WootingKeyboard.IsWootingIds(WootingKeyboard.LegacyVendorId, pid));
            var model = WootingKeyboard.ModelOf(WootingKeyboard.LegacyVendorId, pid);
            Assert.NotNull(model);
            Assert.Contains(model.FormFactor, ValidFormFactors);
            Assert.True(model.LegacyV1);
            Assert.False(model.HallEffect);
            Assert.False(model.HasRapidTrigger);
            Assert.True(model.HasRgb);
            Assert.Equal(1.5, model.MinActuationMm);
            Assert.Equal(3.6, model.MaxActuationMm);
        }

        Assert.False(WootingKeyboard.IsWootingIds(WootingKeyboard.LegacyVendorId, 0x2000));
        Assert.False(WootingKeyboard.IsWootingIds(0x046D, 0x1342));
        Assert.Null(WootingKeyboard.ModelOf(WootingKeyboard.LegacyVendorId, 0x2000));
    }

    [Theory]
    [InlineData(0x31E3, 0x1100, "Wooting One", "tkl", false, false, true, false, 1000)]
    [InlineData(0x31E3, 0x1200, "Wooting Two", "full", false, false, true, false, 1000)]
    [InlineData(0x31E3, 0x1210, "Wooting Two Lekker Edition", "full", true, true, true, false, 1000)]
    [InlineData(0x31E3, 0x1220, "Wooting Two HE (AVR)", "full", true, true, true, false, 1000)]
    [InlineData(0x31E3, 0x1232, "Wooting Two HE", "full", true, true, true, true, 1000)]
    [InlineData(0x31E3, 0x1300, "Wooting 60HE (AVR)", "60", true, true, true, false, 1000)]
    [InlineData(0x31E3, 0x1311, "Wooting 60HE", "60", true, true, true, true, 1000)]
    [InlineData(0x31E3, 0x1322, "Wooting 60HE+", "60", true, true, true, true, 1000)]
    [InlineData(0x31E3, 0x1340, "Wooting 60HE v2", "60", true, true, true, true, 8000)]
    [InlineData(0x31E3, 0x1342, "Wooting 60HE v2", "60", true, true, true, true, 8000)]
    [InlineData(0x31E3, 0x1400, "Wooting 80HE", "80", true, true, true, true, 8000)]
    [InlineData(0x31E3, 0x1412, "Wooting 80HE+", "80", true, true, true, true, 8000)]
    [InlineData(0x31E3, 0x1500, "Wooting UwU", "keypad3", true, true, false, true, 1000)]
    [InlineData(0x31E3, 0x1512, "Wooting UwU RGB", "keypad3", true, true, true, true, 1000)]
    [InlineData(0x03EB, 0xFF01, "Wooting One", "tkl", false, false, true, false, 1000)]
    [InlineData(0x03EB, 0xFF02, "Wooting Two", "full", false, false, true, false, 1000)]
    public void ModelOf_ExposesExpectedCapabilitiesPerModel(
        int vid,
        int pid,
        string name,
        string formFactor,
        bool hallEffect,
        bool hasRt,
        bool hasRgb,
        bool multiReport,
        int maxHz)
    {
        var model = WootingKeyboard.ModelOf(vid, pid);
        Assert.NotNull(model);
        Assert.Equal(name, model.Name);
        Assert.Equal(formFactor, model.FormFactor);
        Assert.Equal(hallEffect, model.HallEffect);
        Assert.Equal(hasRt, model.HasRapidTrigger);
        Assert.Equal(hasRgb, model.HasRgb);
        Assert.Equal(multiReport, model.MultiReport);
        Assert.Equal(maxHz, model.MaxScanHz);
    }

    [Fact]
    public void EncodeCommands_MatchGoldenArmMultiReportAndStandardBytes()
    {
        Assert.Equal(
            [0x01, 0xD1, 0xDA, 0x21, 0x00, 0x00, 0x00, 0x00],
            WootingKeyboard.EncodeCommand(WootingKeyboard.CmdWootDevInit, 0, 0, 0, 0, multiReport: true));

        Assert.Equal(
            [0x01, 0xD1, 0xDA, 0x17, 0x02, 0x00, 0x00, 0x00],
            WootingKeyboard.EncodeProfileCommand(WootingKeyboard.CmdActivateProfile, 2, multiReport: true));

        Assert.Equal(
            [0x01, 0xD1, 0xDA, 0x26, 0x01, 0x00, 0x00, 0x00],
            WootingKeyboard.EncodeProfileCommand(WootingKeyboard.CmdReloadProfile, 1, multiReport: true));

        Assert.Equal(
            [0x00, 0xD0, 0xDA, 0x17, 0x00, 0x00, 0x00, 0x02],
            WootingKeyboard.EncodeProfileCommand(WootingKeyboard.CmdActivateProfile, 2, multiReport: false));

        Assert.Equal(
            [0x00, 0xD0, 0xDA, 0x26, 0x00, 0x00, 0x00, 0x01],
            WootingKeyboard.EncodeProfileCommand(WootingKeyboard.CmdReloadProfile, 1, multiReport: false));
    }

    [Fact]
    public void EncodeProfileOutputReport_PadsToChannelOutputLengthAndSelectsReportIdByDescriptor()
    {
        var padded60HeV2 = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            profileIndex: 0,
            save: true,
            Real60HeV2ProfileBody,
            multiReport: true,
            outputReportByteLength: 2047);

        Assert.Equal(2047, padded60HeV2.Length);
        Assert.Equal([0x01, 0xD1, 0xDA, 0x11, 0x01, 0x16, 0x00], padded60HeV2[..7]);
        Assert.Equal(Real60HeV2ProfileBody, padded60HeV2[7..(7 + Real60HeV2ProfileBody.Length)]);
        Assert.All(padded60HeV2[(7 + Real60HeV2ProfileBody.Length)..], b => Assert.Equal(0, b));

        var mediumPayload = new byte[45];
        Array.Fill(mediumPayload, (byte)0x5A);
        var mediumReport = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            profileIndex: 2,
            save: true,
            mediumPayload,
            multiReport: true,
            outputReportByteLength: 2047);

        Assert.Equal(2047, mediumReport.Length);
        Assert.Equal([0x02, 0xD1, 0xDA, 0x11, 0x05, 0x2D, 0x00], mediumReport[..7]);
        Assert.Equal(mediumPayload, mediumReport[7..(7 + mediumPayload.Length)]);

        var valueCapsBuf = BuildFakeValueCaps([(4, 254), (5, 510)]);
        var customDescriptor = WootingKeyboard.ParseOutputReportSizes(valueCapsBuf, 2);
        var largePayload = new byte[200];
        Array.Fill(largePayload, (byte)0xA5);
        var customReport = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            profileIndex: 1,
            save: false,
            largePayload,
            multiReport: true,
            outputReportByteLength: 511,
            reportSizes: customDescriptor);

        Assert.Equal(511, customReport.Length);
        Assert.Equal([0x04, 0xD1, 0xDA, 0x11, 0x02, 0xC8, 0x00], customReport[..7]);

        var stdBig = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            profileIndex: 1,
            save: true,
            Real60HeV2ProfileBody,
            multiReport: false,
            outputReportByteLength: 257);

        Assert.Equal(257, stdBig.Length);
        Assert.Equal([0x00, 0xD0, 0xDA, 0x11, 0x03, 0x16], stdBig[..6]);
        Assert.Equal(Real60HeV2ProfileBody, stdBig[6..(6 + Real60HeV2ProfileBody.Length)]);

        var stdLegacyV1 = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            profileIndex: 0,
            save: true,
            Real60HeV2ProfileBody,
            multiReport: false,
            outputReportByteLength: 129);

        Assert.Equal(129, stdLegacyV1.Length);
        Assert.Equal([0x00, 0xD0, 0xDA, 0x11, 0x01, 0x16], stdLegacyV1[..6]);
        Assert.Equal(Real60HeV2ProfileBody, stdLegacyV1[6..(6 + Real60HeV2ProfileBody.Length)]);
    }

    [Fact]
    public void SplitOutputPackets_HandlesMultiReportBigPacketAndPreV212SmallPackets()
    {
        var multiFrame = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            0,
            true,
            Real60HeV2ProfileBody,
            multiReport: true,
            outputReportByteLength: 2047);
        var multiPackets = WootingKeyboard.SplitOutputPackets(multiFrame, 2047, multiReport: true);
        Assert.Single(multiPackets);
        Assert.Equal(2047, multiPackets[0].Length);

        var stdBig = WootingKeyboard.EncodeProfileOutputReport(
            WootingKeyboard.OutSetKeyboardBase,
            0,
            true,
            Real60HeV2ProfileBody,
            multiReport: false,
            outputReportByteLength: 257);
        var bigPackets = WootingKeyboard.SplitOutputPackets(stdBig, 257, multiReport: false);
        Assert.Single(bigPackets);
        Assert.Equal(257, bigPackets[0].Length);

        var smallPackets = WootingKeyboard.SplitOutputPackets(stdBig, 65, multiReport: false);
        Assert.Equal(4, smallPackets.Count);
        foreach (var pkt in smallPackets)
        {
            Assert.Equal(65, pkt.Length);
            Assert.Equal(0x00, pkt[0]);
        }
        Assert.Equal(stdBig[1..65], smallPackets[0][1..65]);
        Assert.Equal(stdBig[65..129], smallPackets[1][1..65]);
    }

    [Fact]
    public void Decoders_ParseReal60HeV2AndLegacyHardwareReplies()
    {
        var realVer = Convert.FromHexString("01D1DA01880900020F00626574612E30");
        Assert.Equal("2.15.0-beta.0", WootingKeyboard.DecodeVersion(WootingKeyboard.NormalizeReply(realVer, true), true));

        byte[] releaseVer = [0x01, 0xD1, 0xDA, 0x01, 0x88, 0x03, 0x00, 0x02, 0x0E, 0x01];
        Assert.Equal("2.14.1", WootingKeyboard.DecodeVersion(WootingKeyboard.NormalizeReply(releaseVer, true), true));

        byte[] stdVer = [0x00, 0xD0, 0xDA, 0x01, 0x88, 0x02, 0x0A, 0x04];
        Assert.Equal("2.10.4", WootingKeyboard.DecodeVersion(WootingKeyboard.NormalizeReply(stdVer, false), false));

        var realIdx = Convert.FromHexString("01D1DA0B8802000000");
        Assert.Equal((0, 0), WootingKeyboard.DecodeProfileIndex(WootingKeyboard.NormalizeReply(realIdx, true), true));

        byte[] idxSlot2 = [0x01, 0xD1, 0xDA, 0x0B, 0x88, 0x02, 0x00, 0x02, 0x01];
        Assert.Equal((1, 2), WootingKeyboard.DecodeProfileIndex(WootingKeyboard.NormalizeReply(idxSlot2, true), true));

        byte[] stdIdx = [0x00, 0xD0, 0xDA, 0x0B, 0x88, 0x01, 0x00];
        Assert.Equal((1, 1), WootingKeyboard.DecodeProfileIndex(WootingKeyboard.NormalizeReply(stdIdx, false), false));

        var realConfig = Convert.FromHexString("01D1DA1388070000000000DC030C");
        Assert.Equal("ansi-split", WootingKeyboard.DecodeDeviceLayout(WootingKeyboard.NormalizeReply(realConfig, true), true));
        Assert.Equal((byte)3, Peripherals.LayoutByte(realConfig));

        byte[] legacyV1Config = [0xD0, 0xDA, 0x13, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01];
        Assert.Equal((byte)1, Peripherals.LayoutByte(legacyV1Config, v1: true));
        Assert.Equal("iso", WootingKeyboard.DecodeDeviceLayout(WootingKeyboard.NormalizeReply(legacyV1Config, false), false, legacyV1: true));

        byte[] stdV2Config = [0xD0, 0xDA, 0x13, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02];
        Assert.Equal((byte)2, Peripherals.LayoutByte(stdV2Config, v1: false));
        Assert.Equal("jis", WootingKeyboard.DecodeDeviceLayout(WootingKeyboard.NormalizeReply(stdV2Config, false), false, legacyV1: false));

        byte[] metaRaw = [0x02, 0xD1, 0xDA, 0x37, 0x88, 0x09, 0x00, 0x0A, 0x07, (byte)'G', (byte)'e', (byte)'n', (byte)'e', (byte)'r', (byte)'a', (byte)'l'];
        Assert.Equal((true, "General"), WootingKeyboard.DecodeProfileMetadata(WootingKeyboard.NormalizeReply(metaRaw, true), true));

        byte[] emptyMeta = [0x01, 0xD1, 0xDA, 0x37, 0x88, 0x00, 0x00];
        Assert.Equal((false, null), WootingKeyboard.DecodeProfileMetadata(WootingKeyboard.NormalizeReply(emptyMeta, true), true));
    }

    [Fact]
    public void KeyboardProfileAndRgbCore_DecodeAndPatchPreservingUnknownFieldsAndTachyonField9()
    {
        var profileReply = WrapMultiReply(WootingKeyboard.CmdGetKeyboardProfile, Real60HeV2ProfileBody);
        var decoded = WootingKeyboard.DecodeKeyboardProfile(profileReply, multiReport: true);
        Assert.NotNull(decoded);
        Assert.Equal(3.00, decoded.ActuationMm);
        Assert.True(decoded.DigitalOutput);
        Assert.True(decoded.RapidTrigger);
        Assert.Equal(0.15, decoded.RapidTriggerMm);
        Assert.True(decoded.Tachyon);
        Assert.True(decoded.TachyonMode);
        Assert.Equal(2, decoded.TickRate);
        Assert.True(decoded.ContinuousRapidTrigger);

        var patched = WootingKeyboard.PatchKeyboardProfile(decoded.RawBody, actuationMm: 0.40, rapidTrigger: true, rapidTriggerMm: 0.25, tachyon: false);
        var after = WootingKeyboard.DecodeKeyboardProfile(WrapMultiReply(WootingKeyboard.CmdGetKeyboardProfile, patched), multiReport: true);
        Assert.NotNull(after);
        Assert.Equal(0.40, after.ActuationMm);
        Assert.True(after.RapidTrigger);
        Assert.Equal(0.25, after.RapidTriggerMm);
        Assert.False(after.Tachyon);
        Assert.False(after.TachyonMode);
        Assert.Equal(2, after.TickRate);
        Assert.True(after.ContinuousRapidTrigger);

        var rgbReply = WrapMultiReply(WootingKeyboard.CmdGetRgbProfileCore, Real60HeV2RgbBody);
        var rgbDecoded = WootingKeyboard.DecodeRgbCore(rgbReply, multiReport: true);
        Assert.NotNull(rgbDecoded);
        Assert.Equal(70, rgbDecoded.BrightnessPercent);
        Assert.Equal(179, rgbDecoded.BrightnessRaw);

        var rgbPatched = WootingKeyboard.PatchRgbCore(rgbDecoded.RawBody, 50);
        var rgbAfter = WootingKeyboard.DecodeRgbCore(WrapMultiReply(WootingKeyboard.CmdGetRgbProfileCore, rgbPatched), multiReport: true);
        Assert.NotNull(rgbAfter);
        Assert.Equal(50, rgbAfter.BrightnessPercent);
        Assert.Equal(Real60HeV2RgbBody[6..], rgbPatched[6..]);
    }

    [Fact]
    public void UpdateProfile_SequencesInitWriteSaveReloadAndVerifiesReadBack()
    {
        var transport = new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody);
        var card = MakeCard(WootingKeyboard.VendorId, 0x1342, "Wooting 60HE v2", 0xFF55);

        var result = WootingKeyboard.UpdateProfile(
            card,
            actuationMm: 1.20,
            rapidTrigger: true,
            rapidTriggerMm: 0.25,
            tachyon: false,
            rgbBrightness: 50,
            transport);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1.20, result.ActuationMm);
        Assert.Equal(0.25, result.RapidTriggerMm);
        Assert.False(result.Tachyon);
        Assert.Equal(50, result.RgbBrightness);
        Assert.Equal("60", result.FormFactor);
        Assert.Equal("ansi-split", result.LayoutCode);

        Assert.Contains(WootingKeyboard.CmdWootDevInit, transport.FeatureCommands);
        Assert.Contains(WootingKeyboard.CmdSaveRgbProfile, transport.FeatureCommands);
        Assert.Contains(WootingKeyboard.CmdReloadProfile, transport.FeatureCommands);
        Assert.Equal(2, transport.OutputReports.Count);
        Assert.All(transport.OutputReports, r => Assert.Equal(2047, r.Length));
        Assert.Equal(WootingKeyboard.OutSetKeyboardBase, transport.OutputReports[0][3]);
        Assert.Equal(WootingKeyboard.OutSetRgbProfileCore, transport.OutputReports[1][3]);

        var initIdx = transport.Events.IndexOf("FEAT:21");
        var baseOutIdx = transport.Events.IndexOf("OUT:11");
        var rgbOutIdx = transport.Events.IndexOf("OUT:16");
        var saveRgbIdx = transport.Events.IndexOf("FEAT:08");
        var reloadIdx = transport.Events.IndexOf("FEAT:26");
        Assert.True(initIdx >= 0 && initIdx < baseOutIdx && baseOutIdx < rgbOutIdx && rgbOutIdx < saveRgbIdx && saveRgbIdx < reloadIdx);
    }

    [Fact]
    public void UpdateProfile_SurfacesReadBackMismatchFirmwareRejectionAndWriteFailure()
    {
        var card = MakeCard(WootingKeyboard.VendorId, 0x1342, "Wooting 60HE v2", 0xFF55);

        var ignoredTransport = new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody) { IgnoreWrites = true };
        var mismatch = WootingKeyboard.UpdateProfile(card, actuationMm: 1.00, rapidTrigger: null, rapidTriggerMm: null, tachyon: null, rgbBrightness: null, ignoredTransport);
        Assert.False(mismatch.Ok);
        Assert.Contains("didn't stick", mismatch.Message, StringComparison.OrdinalIgnoreCase);

        var rejectReload = new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody) { RejectCommand = WootingKeyboard.CmdReloadProfile };
        var rejected = WootingKeyboard.UpdateProfile(card, actuationMm: 1.00, rapidTrigger: null, rapidTriggerMm: null, tachyon: null, rgbBrightness: null, rejectReload);
        Assert.False(rejected.Ok);
        Assert.Contains("0x26", rejected.Message, StringComparison.OrdinalIgnoreCase);

        var ioFail = new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody) { ThrowOnOutput = new IOException("HID write pipe closed") };
        var failed = WootingKeyboard.UpdateProfile(card, actuationMm: 1.00, rapidTrigger: null, rapidTriggerMm: null, tachyon: null, rgbBrightness: null, ioFail);
        Assert.False(failed.Ok);
        Assert.Contains("HID write pipe closed", failed.Message, StringComparison.OrdinalIgnoreCase);

        var uwuNoRgb = MakeCard(WootingKeyboard.VendorId, 0x1500, "Wooting UwU", 0xFF55);
        var noRgbRes = WootingKeyboard.UpdateProfile(uwuNoRgb, null, null, null, null, rgbBrightness: 80, new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody));
        Assert.False(noRgbRes.Ok);
        Assert.Contains("RGB", noRgbRes.Message, StringComparison.OrdinalIgnoreCase);

        var legacy1337 = MakeCard(WootingKeyboard.VendorId, 0x1300, "Wooting 60HE (AVR)", 0x1337);
        var legacyRes = WootingKeyboard.UpdateProfile(legacy1337, 1.0, null, null, null, null, new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody));
        Assert.False(legacyRes.Ok);
        Assert.Contains("0xFF55", legacyRes.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchProfile_SendsInitActivateReloadAndDetectsStuckSlot()
    {
        var card = MakeCard(WootingKeyboard.VendorId, 0x1342, "Wooting 60HE v2", 0xFF55);
        var transport = new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody);

        var switched = WootingKeyboard.SwitchProfile(card, 2, transport);
        Assert.True(switched.Ok, switched.Message);
        Assert.Equal(2, switched.ActiveProfile);
        Assert.True(transport.Events.IndexOf("FEAT:21") < transport.Events.IndexOf("FEAT:17"));
        Assert.True(transport.Events.IndexOf("FEAT:17") < transport.Events.IndexOf("FEAT:26"));

        var stuck = new FakeWootingTransport(Real60HeV2ProfileBody, Real60HeV2RgbBody) { IgnoreProfileSwitch = true };
        var stuckRes = WootingKeyboard.SwitchProfile(card, 2, stuck);
        Assert.False(stuckRes.Ok);
        Assert.Contains("didn't take effect", stuckRes.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreBackup_WritesBaseAndRgbAndVerifiesByteForByte()
    {
        var card = MakeCard(WootingKeyboard.VendorId, 0x1342, "Wooting 60HE v2", 0xFF55);
        var modifiedBase = WootingKeyboard.PatchKeyboardProfile(Real60HeV2ProfileBody, 1.00, true, 0.25, false);
        var modifiedRgb = WootingKeyboard.PatchRgbCore(Real60HeV2RgbBody, 35);
        var transport = new FakeWootingTransport(modifiedBase, modifiedRgb);

        var backup = new WootingKeyboard.KeyboardBackupFile(
            SchemaVersion: 2,
            Model: "Wooting 60HE v2",
            ProductId: 0x1342,
            Firmware: "2.15.0-beta.0",
            CreatedUtc: "2026-11-22T20:00:00.0000000Z",
            ActiveProfile: 0,
            Profiles:
            [
                new(0, "General", Convert.ToBase64String(Real60HeV2ProfileBody), Convert.ToBase64String(Real60HeV2RgbBody)),
            ]);

        var restored = WootingKeyboard.RestoreBackupData(card, backup, transport);
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(Real60HeV2ProfileBody, transport.SlotProfiles[0]);
        Assert.Equal(Real60HeV2RgbBody, transport.SlotRgb[0]);
        Assert.Equal(3.00, restored.ActuationMm);
        Assert.Equal(70, restored.RgbBrightness);

        var failingTransport = new FakeWootingTransport(modifiedBase, modifiedRgb) { IgnoreWrites = true };
        var failedRestore = WootingKeyboard.RestoreBackupData(card, backup, failingTransport);
        Assert.False(failedRestore.Ok);
        Assert.Contains("verification failed", failedRestore.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backups_RoundTripJsonValidateFileNameAndRegisterPlayerData()
    {
        var backup = new WootingKeyboard.KeyboardBackupFile(
            SchemaVersion: 2,
            Model: "Wooting 60HE v2",
            ProductId: 0x1342,
            Firmware: "2.15.0-beta.0",
            CreatedUtc: "2026-11-21T16:00:00.0000000Z",
            ActiveProfile: 1,
            Profiles:
            [
                new(0, "Digital", Convert.ToBase64String([0x0A, 0x02, 0x08, 0x01]), Convert.ToBase64String([0x0A, 0x02, 0x08, 0x64])),
                new(1, "FPS", Convert.ToBase64String([0x0A, 0x02, 0x08, 0x02])),
            ]);

        var json = WootingKeyboard.SerializeBackup(backup);
        var restored = WootingKeyboard.DeserializeBackup(json);
        Assert.NotNull(restored);
        Assert.Equal("Wooting 60HE v2", restored.Model);
        Assert.Equal(0x1342, restored.ProductId);
        Assert.Equal(1, restored.ActiveProfile);
        Assert.Equal(2, restored.Profiles.Count);
        Assert.Equal("FPS", restored.Profiles[1].Name);
        Assert.NotNull(restored.Profiles[0].RgbCoreBase64);
        Assert.Null(restored.Profiles[1].RgbCoreBase64);

        Assert.Null(WootingKeyboard.ResolveBackupPath(null));
        Assert.Null(WootingKeyboard.ResolveBackupPath("../wooting-1342-20261121-160000.json"));
        Assert.Null(WootingKeyboard.ResolveBackupPath("sub/wooting-1342-20261121-160000.json"));
        Assert.Null(WootingKeyboard.ResolveBackupPath("evil.json"));
        Assert.NotNull(WootingKeyboard.ResolveBackupPath("wooting-1342-20261121-160000.json"));
        Assert.NotNull(WootingKeyboard.ResolveBackupPath("wooting-1342-20261121-160000-01.json"));

        Assert.NotNull(PlayerData.Of("keyboard-backups/wooting-1342-20261121-160000.json"));
    }

    private static byte[] BuildFakeValueCaps((byte ReportId, int BodyBytes)[] entries)
    {
        var buf = new byte[entries.Length * 72];
        for (var i = 0; i < entries.Length; i++)
        {
            var off = i * 72;
            buf[off + 2] = entries[i].ReportId;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 18, 2), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 20, 2), (ushort)entries[i].BodyBytes);
        }
        return buf;
    }

    private static Peripherals.Peripheral MakeCard(int vid, int pid, string name, int page) =>
        new(
            Id: $"wooting-{vid:X4}-{pid:X4}",
            Kind: "keyboard",
            Name: name,
            Maker: "Wooting",
            Model: WootingKeyboard.ModelOf(vid, pid)?.FormFactor,
            Connection: "usb",
            Role: null,
            Battery: null,
            Color: null,
            Layout: null)
        {
            VendorId = vid,
            ProductId = pid,
            Collections = [new Peripherals.Collection($"HID\\VID_{vid:X4}&PID_{pid:X4}&MI_02\\TEST", page, 1)],
        };

    private static byte[] WrapMultiReply(byte cmd, ReadOnlySpan<byte> body)
    {
        var reply = new byte[6 + body.Length];
        reply[0] = 0xD1;
        reply[1] = 0xDA;
        reply[2] = cmd;
        reply[3] = WootingKeyboard.StatusOk;
        reply[4] = (byte)(body.Length & 0xFF);
        reply[5] = (byte)((body.Length >> 8) & 0xFF);
        body.CopyTo(reply.AsSpan(6));
        return reply;
    }

    private sealed class FakeWootingTransport : WootingKeyboard.IWootingTransport
    {
        public Dictionary<int, byte[]> SlotProfiles { get; } = [];
        public Dictionary<int, byte[]> SlotRgb { get; } = [];
        public int ActiveProfile { get; set; }
        public bool IgnoreWrites { get; set; }
        public bool IgnoreProfileSwitch { get; set; }
        public byte? RejectCommand { get; set; }
        public Exception? ThrowOnOutput { get; set; }
        public List<byte> FeatureCommands { get; } = [];
        public List<byte[]> OutputReports { get; } = [];
        public List<string> Events { get; } = [];

        public FakeWootingTransport(byte[] initialProfile, byte[] initialRgb)
        {
            SlotProfiles[0] = initialProfile.ToArray();
            SlotRgb[0] = initialRgb.ToArray();
        }

        public WootingKeyboard.HidChannelInfo Inspect(string path) =>
            new(2047, 2047, 8, [new(1, 32), new(2, 62), new(3, 254), new(4, 510), new(5, 1022), new(6, 2046)]);

        public byte[]? TransactFeature(string path, bool multi, byte[] payload, byte expectedCmd)
        {
            var cmd = payload[3];
            var slot = multi ? payload[4] : payload[7];
            FeatureCommands.Add(cmd);
            Events.Add($"FEAT:{cmd:X2}");

            if (RejectCommand == cmd)
                return [0xD1, 0xDA, cmd, 0x66, 0x00, 0x00];

            return cmd switch
            {
                WootingKeyboard.CmdGetVersion => WootingKeyboard.NormalizeReply(Convert.FromHexString("01D1DA01880900020F00626574612E30"), true),
                WootingKeyboard.CmdGetDeviceConfig => WootingKeyboard.NormalizeReply(Convert.FromHexString("01D1DA1388070000000000DC030C"), true),
                WootingKeyboard.CmdGetCurrentProfileIndex => [0xD1, 0xDA, 0x0B, 0x88, 0x02, 0x00, (byte)ActiveProfile, 0x00],
                WootingKeyboard.CmdGetProfileMetadata => slot == 0
                    ? [0xD1, 0xDA, 0x37, 0x88, 0x09, 0x00, 0x0A, 0x07, (byte)'G', (byte)'e', (byte)'n', (byte)'e', (byte)'r', (byte)'a', (byte)'l']
                    : [0xD1, 0xDA, 0x37, 0x88, 0x00, 0x00],
                WootingKeyboard.CmdGetKeyboardProfile => SlotProfiles.TryGetValue(slot, out var p)
                    ? WrapMultiReply(cmd, p)
                    : WrapMultiReply(cmd, SlotProfiles[0]),
                WootingKeyboard.CmdGetRgbProfileCore => SlotRgb.TryGetValue(slot, out var rgb)
                    ? WrapMultiReply(cmd, rgb)
                    : WrapMultiReply(cmd, SlotRgb[0]),
                WootingKeyboard.CmdActivateProfile => HandleSwitch(slot),
                WootingKeyboard.CmdWootDevInit or WootingKeyboard.CmdReloadProfile or WootingKeyboard.CmdSaveRgbProfile =>
                    [0xD1, 0xDA, cmd, 0x88, 0x00, 0x00],
                _ => [0xD1, 0xDA, cmd, 0x66, 0x00, 0x00],
            };
        }

        public void WriteOutput(string path, byte[] framedReport, bool multi)
        {
            var cmd = framedReport[3];
            OutputReports.Add(framedReport.ToArray());
            Events.Add($"OUT:{cmd:X2}");
            if (ThrowOnOutput is not null) throw ThrowOnOutput;
            if (IgnoreWrites) return;

            var flags = framedReport[4];
            var slot = (flags >> 1) & 0x0F;
            var len = framedReport[5] | (framedReport[6] << 8);
            var payload = framedReport.AsSpan(7, len).ToArray();
            if (cmd == WootingKeyboard.OutSetKeyboardBase) SlotProfiles[slot] = payload;
            else if (cmd == WootingKeyboard.OutSetRgbProfileCore) SlotRgb[slot] = payload;
        }

        public void Delay(int milliseconds) { }

        private byte[] HandleSwitch(int slot)
        {
            if (!IgnoreProfileSwitch) ActiveProfile = slot;
            return [0xD1, 0xDA, WootingKeyboard.CmdActivateProfile, 0x88, 0x00, 0x00];
        }
    }
}
