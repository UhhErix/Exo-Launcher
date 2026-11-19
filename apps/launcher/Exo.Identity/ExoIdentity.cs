using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// Whether this PC is part of an Exo identity, with plan §2.9's rule for a lost key: when this app's state says the PC
/// was linked but its keys are gone or different (a TPM clear, a BIOS reset that wiped a TPM key, a reset Windows
/// profile), it is "link this PC again", never a silent new identity. Checking only opens keys; they are made only when
/// the person starts setup or a link (<see cref="KeysForSetup"/>), and the PC then joins as a new device while its old
/// record stays in the list for another PC to remove. <c>identity.json</c> holds no secret: the Exo ID, the device id
/// and a hash of the encryption key, so a replaced encryption key is caught as well.
/// </summary>
public sealed class ExoIdentity(IDeviceKeyStore keys, string statePath)
{
    public enum Status { NotSetUp, Ready, LinkAgain }

    /// <param name="DeviceId">This PC's id in the list when it was linked; after a lost key, the record to remove.</param>
    /// <param name="EncKey">The first 16 bytes of SHA-256 of the encryption key it was linked with, in hex.</param>
    public sealed record Linked(string ExoId, string DeviceId, string EncKey);

    /// <summary>What this PC is now, and what it was linked as. Never creates a key.</summary>
    public (Status Status, Linked? Linked) Check()
    {
        if (Read() is not { } linked) return (Status.NotSetUp, null);
        using var found = keys.Open();
        var same = found is not null && found.DeviceId == linked.DeviceId && EncKeyOf(found) == linked.EncKey;
        return (same ? Status.Ready : Status.LinkAgain, linked);
    }

    /// <summary>The keys to set this PC up or link it with: made when missing. Only those two flows call it.</summary>
    public DeviceKeys KeysForSetup() => keys.OpenOrCreate();

    /// <summary>Written once the device list holds this PC.</summary>
    public void SaveLinked(string exoId, DeviceKeys linked)
    {
        var json = new JsonObject { ["v"] = 1, ["exoId"] = exoId, ["device"] = linked.DeviceId, ["encKey"] = EncKeyOf(linked) };
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temp = statePath + ".tmp";
        File.WriteAllBytes(temp, CanonicalJson.Serialize(json));
        File.Move(temp, statePath, overwrite: true);
    }

    private Linked? Read()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(statePath); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        // A state file that doesn't read still says this PC was linked once: it never reads as never set up.
        if (CanonicalJson.ParseObject(bytes) is not { } json || Device.Int(json, "v") != 1 || Device.Text(json, "exoId") is not { } exoId ||
            Device.Text(json, "device") is not { } device || Device.Text(json, "encKey") is not { } encKey)
            return new("", "", "");
        return new(exoId, device, encKey);
    }

    private static string EncKeyOf(DeviceKeys keys) => Convert.ToHexStringLower(SHA256.HashData(keys.Encryption.PublicKey).AsSpan(0, 16));
}
