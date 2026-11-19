using System.Runtime.InteropServices;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services;

/// <summary>
/// One audio endpoint as Windows has it, for the audio page in Devices: the format it mixes at, its volume and mute, the
/// formats it really takes, and the changes the page asks for. Formats are asked of the device (IAudioClient's exclusive
/// check over the usual rates and bit depths for its channel count); a new one goes through IPolicyConfig, the undocumented
/// interface Windows' own Sound settings use, and is read back from PKEY_AudioEngine_DeviceFormat to say whether it took.
/// There is no live level here: a microphone's meter stays at zero unless an app is capturing, so it would say nothing
/// true when you look at it. Covered by ControllerMemoryTests and AudioDetailsTests.
/// </summary>
internal static class AudioDetails
{
    /// <param name="Rate">Samples a second.</param>
    /// <param name="Bits">Bits a sample that carry sound (a 32-bit container may carry 24).</param>
    /// <param name="Channels">How many it mixes for.</param>
    internal sealed record Format(int Rate, int Bits, int Channels);

    /// <param name="Volume">Windows' volume for it, 0 to 100.</param>
    internal sealed record Endpoint(Format? Format, int? Volume, bool? Muted);

    /// <summary>A WAVEFORMATEX or WAVEFORMATEXTENSIBLE; null when it isn't one.</summary>
    internal static Format? FormatOf(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 16) return null;
        var tag = BitConverter.ToUInt16(blob[..2]);
        var channels = BitConverter.ToUInt16(blob[2..4]);
        var rate = BitConverter.ToInt32(blob[4..8]);
        var bits = (int)BitConverter.ToUInt16(blob[14..16]);
        if (tag == 0xFFFE && blob.Length >= 20 && BitConverter.ToUInt16(blob[18..20]) is var valid and > 0) bits = valid;
        return channels > 0 && rate > 0 && bits > 0 ? new Format(rate, bits, channels) : null;
    }

    /// <summary>A format the device takes, as the page offers it. Container: the bits a sample occupies (24 may sit in 24 or 32).</summary>
    internal sealed record Offer(string Key, int Rate, int Bits, int Container, int Channels);

    /// <summary>What the page gets to choose from, and which of them it's on.</summary>
    internal sealed record Offers(string? Current, IReadOnlyList<Offer> Formats);

    private static readonly int[] Rates = [44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly (int Bits, int Container)[] Depths = [(16, 16), (24, 24), (24, 32), (32, 32)];

    internal static string KeyOf(int rate, int bits, int container, int channels) => $"{rate}-{bits}-{container}-{channels}";

    /// <summary>The usual rates and bit depths for a channel count, lowest first.</summary>
    internal static IEnumerable<Offer> Candidates(int channels) =>
        from rate in Rates from depth in Depths select new Offer(KeyOf(rate, depth.Bits, depth.Container, channels), rate, depth.Bits, depth.Container, channels);

    /// <summary>
    /// What to offer: each rate and depth the device takes once (24 bits in the first container it takes), and the one it's
    /// on, always, whether or not exclusive mode answers (Windows' "allow exclusive control" off answers no to everything).
    /// </summary>
    internal static Offers OffersOf(IEnumerable<Offer> candidates, Func<Offer, bool> takes, Format? current, int currentContainer)
    {
        var offers = new List<Offer>();
        foreach (var candidate in candidates)
            if (!offers.Any(o => o.Rate == candidate.Rate && o.Bits == candidate.Bits) && takes(candidate)) offers.Add(candidate);
        if (current is null) return new Offers(null, offers);
        var on = offers.FirstOrDefault(o => o.Rate == current.Rate && o.Bits == current.Bits && o.Channels == current.Channels);
        if (on is null)
        {
            on = new Offer(KeyOf(current.Rate, current.Bits, currentContainer, current.Channels), current.Rate, current.Bits, currentContainer, current.Channels);
            offers = [.. offers.Append(on).OrderBy(o => o.Rate).ThenBy(o => o.Bits)];
        }
        return new Offers(on.Key, offers);
    }

    /// <summary>A volume from the page, 0 to 100; null when it isn't a number.</summary>
    internal static int? Level(double? value) => value is { } v && double.IsFinite(v) ? (int)Math.Round(Math.Clamp(v, 0, 100)) : null;

    /// <summary>The speakers a format's channels are, from the current format's mask where it has one that fits.</summary>
    internal static uint MaskFor(ReadOnlySpan<byte> current, int channels)
    {
        if (current.Length >= 24 && BitConverter.ToUInt16(current[..2]) == 0xFFFE && BitConverter.ToUInt16(current[2..4]) == channels) return BitConverter.ToUInt32(current[20..24]);
        return channels switch { 1 => 0x4u, 2 => 0x3u, 6 => 0x3Fu, 8 => 0x63Fu, _ => 0u };
    }

    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71"), Float = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>A WAVEFORMATEXTENSIBLE: 40 bytes, little-endian. A device refuses one whose block size doesn't follow from its channels and container.</summary>
    internal static byte[] WaveFormat(int rate, int bits, int container, int channels, uint mask, bool floating)
    {
        var bytes = new byte[40];
        var block = channels * container / 8;
        var span = bytes.AsSpan();
        BitConverter.TryWriteBytes(span[0..], (ushort)0xFFFE);
        BitConverter.TryWriteBytes(span[2..], (ushort)channels);
        BitConverter.TryWriteBytes(span[4..], rate);
        BitConverter.TryWriteBytes(span[8..], rate * block);
        BitConverter.TryWriteBytes(span[12..], (ushort)block);
        BitConverter.TryWriteBytes(span[14..], (ushort)container);
        BitConverter.TryWriteBytes(span[16..], (ushort)22);
        BitConverter.TryWriteBytes(span[18..], (ushort)bits);
        BitConverter.TryWriteBytes(span[20..], mask);
        (floating ? Float : Pcm).TryWriteBytes(span[24..]);
        return bytes;
    }

    /// <summary>Whether what the device reads back is what was asked for.</summary>
    internal static bool Took(Format? now, Offer asked) => now is not null && now.Rate == asked.Rate && now.Bits == asked.Bits && now.Channels == asked.Channels;

    /// <summary>The formats a device takes, asked of it. Changes nothing.</summary>
    internal static Offers? ListFormats(string id)
    {
        if (Open(id) is not { } device) return null;
        var blob = ReadBlob(device);
        var current = blob is null ? null : FormatOf(blob);
        if (current is null) return new Offers(null, []);
        var iid = typeof(IAudioClient).GUID;
        if (device.Activate(ref iid, 23, 0, out var client) != 0 || client is not IAudioClient audio) return OffersOf([], _ => false, current, BitConverter.ToUInt16(blob.AsSpan(14, 2)));
        try
        {
            var mask = MaskFor(blob, current.Channels);
            return OffersOf(Candidates(current.Channels), offer => Takes(audio, offer, mask), current, BitConverter.ToUInt16(blob.AsSpan(14, 2)));
        }
        finally { Marshal.ReleaseComObject(audio); }
    }

    private const int Exclusive = 1;

    private static bool Takes(IAudioClient audio, Offer offer, uint mask)
    {
        var format = WaveFormat(offer.Rate, offer.Bits, offer.Container, offer.Channels, mask, floating: false);
        var memory = Marshal.AllocCoTaskMem(format.Length);
        try
        {
            Marshal.Copy(format, 0, memory, format.Length);
            return audio.IsFormatSupported(Exclusive, memory, 0) == 0;
        }
        finally { Marshal.FreeCoTaskMem(memory); }
    }

    /// <summary>Sets a device's format as Windows' Sound settings do: the format itself, and the 32-bit float mix at its rate. Null when it took; else why not.</summary>
    internal static string? SetFormat(string id, Offer offer)
    {
        if (Open(id) is not { } device) return "Windows can't find this device.";
        var mask = MaskFor(ReadBlob(device), offer.Channels);
        var endpoint = Marshal.AllocCoTaskMem(40);
        var mix = Marshal.AllocCoTaskMem(40);
        try
        {
            Marshal.Copy(WaveFormat(offer.Rate, offer.Bits, offer.Container, offer.Channels, mask, floating: false), 0, endpoint, 40);
            Marshal.Copy(WaveFormat(offer.Rate, 32, 32, offer.Channels, mask, floating: true), 0, mix, 40);
            var result = ((IPolicyConfig)new PolicyConfigComObject()).SetDeviceFormat(id, endpoint, mix);
            if (result != 0) return "Windows didn't take that format.";
        }
        finally
        {
            Marshal.FreeCoTaskMem(endpoint);
            Marshal.FreeCoTaskMem(mix);
        }
        var now = ReadBlob(device) is { } blob ? FormatOf(blob) : null;
        return Took(now, offer) ? null : "The device stayed on its old format.";
    }

    /// <summary>Sets the endpoint's volume (0-100) in Windows. Whether it took.</summary>
    internal static bool SetVolume(string id, int volume) =>
        Control(id) is { } control && control.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 100) / 100f, 0) >= 0;

    internal static bool SetMute(string id, bool muted) => Control(id) is { } control && control.SetMute(muted, 0) >= 0;

    private static IMMDevice? Open(string id) =>
        Activator.CreateInstance(typeof(MMDeviceEnumeratorComObject)) is IMMDeviceEnumerator devices && devices.GetDevice(id, out var device) == 0 ? device : null;

    private static IAudioEndpointVolume? Control(string id)
    {
        if (Open(id) is not { } device) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        return device.Activate(ref iid, 23, 0, out var control) == 0 ? control as IAudioEndpointVolume : null;
    }

    internal static Endpoint? Read(string id)
    {
        if (Open(id) is not { } device) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        int? volume = null;
        bool? muted = null;
        if (device.Activate(ref iid, 23, 0, out var control) == 0 && control is IAudioEndpointVolume endpoint)
        {
            if (endpoint.GetMasterVolumeLevelScalar(out var level) >= 0) volume = (int)Math.Round(level * 100);
            if (endpoint.GetMute(out var mute) >= 0) muted = mute;
        }
        return new Endpoint(ReadBlob(device) is { } blob ? FormatOf(blob) : null, volume, muted);
    }

    /// <summary>The device format as Windows keeps it, a WAVEFORMATEX or WAVEFORMATEXTENSIBLE.</summary>
    private static byte[]? ReadBlob(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var pointer) != 0 || pointer == 0) return null;
        try
        {
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(pointer);
            var key = DeviceFormat;
            var value = new PropVariant();
            if (store.GetValue(ref key, ref value) != 0) return null;
            try
            {
                if (value.Type != 65 || value.Extra == 0) return null; // VT_BLOB: its size, then a pointer
                var size = Math.Min((int)(uint)value.Pointer, 64);
                var blob = new byte[size];
                Marshal.Copy(value.Extra, blob, 0, size);
                return blob;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.Release(pointer); }
    }

    // PKEY_AudioEngine_DeviceFormat: the format the audio engine mixes at.
    private static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public nint Extra;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, nint context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, nint context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>As far as the check Exo uses; the rest of the interface isn't called.</summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        // Exclusive mode takes no closest match: null, or it answers E_POINTER.
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, nint closest);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigComObject;

    /// <summary>Windows' own policy interface (undocumented, its order the same since Windows 7), up to the call Exo makes.</summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint endpointFormat, nint mixFormat);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
