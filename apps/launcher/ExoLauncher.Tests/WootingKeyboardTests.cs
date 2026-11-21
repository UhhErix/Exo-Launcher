using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class WootingKeyboardTests
{
    [Theory]
    [InlineData(0x1342, "Wooting 60HE v2", true, 8000)]
    [InlineData(0x1340, "Wooting 60HE v2", true, 8000)]
    [InlineData(0x1400, "Wooting 80HE", true, 8000)]
    [InlineData(0x1412, "Wooting 80HE+", true, 8000)]
    [InlineData(0x1322, "Wooting 60HE+", true, 1000)]
    [InlineData(0x1230, "Wooting Two HE", true, 1000)]
    [InlineData(0x1100, "Wooting One", false, 1000)]
    public void ModelOf_IdentifiesCatalogModelsAcrossGamepadModeVariants(int pid, string name, bool hallEffect, int maxHz)
    {
        var model = WootingKeyboard.ModelOf(pid);
        Assert.NotNull(model);
        Assert.Equal(name, model.Name);
        Assert.Equal(hallEffect, model.HallEffect);
        Assert.Equal(maxHz, model.MaxScanHz);
    }

    [Fact]
    public void EncodeCommands_UseArmMultiReportAndStandardFraming()
    {
        var arm = WootingKeyboard.EncodeProfileCommand(WootingKeyboard.CmdActivateProfile, 2, multiReport: true);
        Assert.Equal([0x01, 0xD1, 0xDA, 0x17, 0x02, 0x00, 0x00, 0x00], arm);

        var std = WootingKeyboard.EncodeProfileCommand(WootingKeyboard.CmdActivateProfile, 2, multiReport: false);
        Assert.Equal([0x00, 0xD0, 0xDA, 0x17, 0x00, 0x00, 0x00, 0x02], std);
    }

    [Fact]
    public void Decoders_ParseVersionProfileIndexAndMetadata()
    {
        var ver = WootingKeyboard.NormalizeReply([0x01, 0xD1, 0xDA, 0x01, 0x88, 2, 15, 0], multiReport: true);
        Assert.Equal("2.15.0", WootingKeyboard.DecodeVersion(ver));

        var idx = WootingKeyboard.NormalizeReply([0x01, 0xD1, 0xDA, 0x0B, 0x88, 0, 0, 2], multiReport: true);
        Assert.Equal((0, 2), WootingKeyboard.DecodeProfileIndex(idx));

        byte[] metaRaw = [0x01, 0xD1, 0xDA, 0x37, 0x88, 0x05, 0x00, 0x0A, 0x03, (byte)'F', (byte)'P', (byte)'S'];
        var meta = WootingKeyboard.DecodeProfileMetadata(WootingKeyboard.NormalizeReply(metaRaw, multiReport: true));
        Assert.Equal((true, "FPS"), meta);

        byte[] emptyMeta = [0x01, 0xD1, 0xDA, 0x37, 0x88, 0x00, 0x00];
        Assert.Equal((false, null), WootingKeyboard.DecodeProfileMetadata(WootingKeyboard.NormalizeReply(emptyMeta, multiReport: true)));
    }

    [Fact]
    public void KeyboardProfile_DecodesAndPatchesWithoutDroppingUnknownFields()
    {
        var body = Convert.FromHexString("0A1408B3860110011801209A83012800300238004800");
        var reply = new List<byte> { 0xD1, 0xDA, 0x27, 0x88, (byte)body.Length, 0x00 };
        reply.AddRange(body);

        var decoded = WootingKeyboard.DecodeKeyboardProfile(reply.ToArray(), multiReport: true);
        Assert.NotNull(decoded);
        Assert.Equal(0.20, decoded.ActuationMm);
        Assert.True(decoded.DigitalOutput);
        Assert.True(decoded.RapidTrigger);
        Assert.Equal(0.10, decoded.RapidTriggerMm);
        Assert.False(decoded.Tachyon);
        Assert.Equal(2, decoded.TickRate);

        var patched = WootingKeyboard.PatchKeyboardProfile(decoded.RawBody, actuationMm: 0.40, rapidTrigger: true, rapidTriggerMm: 0.20, tachyon: true);
        var reparsedReply = new List<byte> { 0xD1, 0xDA, 0x27, 0x88, (byte)patched.Length, 0x00 };
        reparsedReply.AddRange(patched);
        var after = WootingKeyboard.DecodeKeyboardProfile(reparsedReply.ToArray(), multiReport: true);
        Assert.NotNull(after);
        Assert.Equal(0.40, after.ActuationMm);
        Assert.True(after.RapidTrigger);
        Assert.Equal(0.20, after.RapidTriggerMm);
        Assert.True(after.Tachyon);
        Assert.Equal(2, after.TickRate);
        Assert.True(after.DigitalOutput);

        var report = WootingKeyboard.EncodeProfileOutputReport(WootingKeyboard.OutSetKeyboardBase, profileIndex: 1, save: true, patched, multiReport: true);
        Assert.Equal(33, report.Length);
        Assert.Equal(1, report[0]);
        Assert.Equal(0xD1, report[1]);
        Assert.Equal(0xDA, report[2]);
        Assert.Equal(0x11, report[3]);
        Assert.Equal(0x03, report[4]);
    }
}
