using System.Runtime.InteropServices;
using ExoCapture.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ExoCapture.Encode;

internal enum Codec { H264, Hevc, Av1, Vp9 }

/// <summary>Rate control: a clip's quality-led variable rate, or Go Live's constant rate at low delay.</summary>
internal enum RateMode { Clip, Stream, Careful }

/// <summary>An encoder Media Foundation lists on this card, or Windows' own on the processor.</summary>
internal sealed record EncoderChoice(Codec Codec, string Name, IMFActivate Activate, bool Hardware);

/// <summary>Encoded output: Annex B for H.264 and HEVC, OBUs for AV1; the time is the capture time it was given (100 ns).</summary>
internal delegate void EncodedHandler(byte[] data, long time, bool key);

/// <summary>
/// A graphics card's video encoder through Media Foundation (NVENC, AMF or Quick Sync, as an asynchronous hardware MFT on
/// the capturing card), fed NV12 or P010 textures straight from the video processor, so frames never touch the processor;
/// or Windows' own H.264 encoder on the processor when the card's will not start. No B-frames, so output comes in order.
/// </summary>
internal sealed unsafe class VideoEncoder : IDisposable
{
    internal static readonly Guid Av1 = VideoFormatGuids.FromFourCC(new FourCC("AV01"));
    private static readonly Guid AdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    private static readonly Guid RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    private static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    private static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid ICodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid IMFShutdown = new("97ec2ea4-0e42-4937-97ac-9d6d328824e1");

    internal readonly EncoderChoice Choice;
    internal readonly int Width, Height, Fps;
    internal readonly bool TenBit;
    internal int Bitrate { get; private set; }
    /// <summary>The parameter sets (Annex B) or AV1 sequence header the encoder named on its output type, if it did.</summary>
    internal byte[]? SequenceHeader { get; private set; }

    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator? _events;
    private readonly GpuDevice _gpu;
    private readonly EncodedHandler _output;
    private readonly ID3D11Texture2D[] _pool;
    private int _next;
    private readonly SemaphoreSlim _credits = new(0, 64);
    private readonly Thread? _pump;
    private readonly ManualResetEventSlim _drained = new(false);
    private volatile bool _closed;
    private int _failures;
    private ID3D11Texture2D? _staging;
    internal string? Failed { get; private set; }

    /// <summary>Encoders for a codec: the card's (matching its LUID) first, by Windows' ranking.</summary>
    internal static List<EncoderChoice> Find(Codec codec, long luid, bool hardware)
    {
        var subtype = Subtype(codec);
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = subtype };
        var flags = hardware ? 0x4u | 0x40u /* HARDWARE | SORTANDFILTER */ : 0x1u | 0x40u /* SYNCMFT | SORTANDFILTER */;
        var list = new List<EncoderChoice>();
        try
        {
            var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, output); // its activates are kept, so not disposed
            foreach (var activate in found)
            {
                if (hardware)
                {
                    var blob = activate.GetBlobSize(AdapterLuid, out var size).Success && size == 8 ? activate.GetBlob(AdapterLuid) : null;
                    if (blob is not null && BitConverter.ToInt64(blob) != luid) { activate.Dispose(); continue; }
                }
                string name;
                try { name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { name = codec + " encoder"; }
                list.Add(new EncoderChoice(codec, name, activate, hardware));
            }
        }
        catch (SharpGen.Runtime.SharpGenException) { }
        return list;
    }

    internal static Guid Subtype(Codec codec) => codec switch { Codec.H264 => VideoFormatGuids.H264, Codec.Hevc => VideoFormatGuids.Hevc, Codec.Vp9 => VideoFormatGuids.Vp90, _ => Av1 };

    /// <summary>Windows can play this codec back (its decoder is installed), so a clip in it opens anywhere on this PC.</summary>
    internal static bool Playable(Codec codec)
    {
        if (codec == Codec.H264) return true;
        try
        {
            using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, 0x1 | 0x2 | 0x4 | 0x40,
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Subtype(codec) }, null);
            var any = false;
            foreach (var a in found) { any = true; a.Dispose(); }
            return any;
        }
        catch (SharpGen.Runtime.SharpGenException) { return false; }
    }

    internal VideoEncoder(GpuDevice gpu, EncoderChoice choice, int width, int height, int fps, int bitrate, RateMode mode, bool tenBit, EncodedHandler output, int gopSeconds = 1)
    {
        (_gpu, Choice, Width, Height, Fps, Bitrate, TenBit, _output) = (gpu, choice, width, height, fps, bitrate, tenBit, output);
        _mft = choice.Activate.ActivateObject<IMFTransform>();
        try
        {
            var attributes = _mft.Attributes;
            if (choice.Hardware)
            {
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)gpu.Manager.NativePointer);
            }
            attributes.Set(new Guid("a634a91c-822b-41b9-a494-4de4643612b0") /* MF_LOW_LATENCY */, mode == RateMode.Stream ? 1u : 0u);
            attributes.Dispose();

            using (var type = MediaFactory.MFCreateMediaType())
            {
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                type.Set(MediaTypeAttributeKeys.Subtype, Subtype(choice.Codec));
                type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /* progressive */);
                type.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(fps * gopSeconds));
                if (choice.Codec != Codec.Vp9)
                    type.Set(MediaTypeAttributeKeys.Mpeg2Profile, choice.Codec switch
                    {
                        Codec.H264 => 100u, // High
                        Codec.Hevc => tenBit ? 2u : 1u, // Main10, Main
                        _ => 0u, // AV1 Main
                    });
                Colour(type, tenBit);
                _mft.SetOutputType(0, type, 0);
            }
            using (var type = MediaFactory.MFCreateMediaType())
            {
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                type.Set(MediaTypeAttributeKeys.Subtype, tenBit ? VideoFormatGuids.P010 : VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
                MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                Colour(type, tenBit);
                _mft.SetInputType(0, type, 0);
            }
            SetCodec(BFrames, 0);
            SetCodec(GopSize, (uint)(fps * gopSeconds));
            switch (mode)
            {
                case RateMode.Stream:
                    SetCodec(RateControl, 0); // CBR
                    SetCodec(MeanBitRate, (uint)bitrate);
                    SetCodec(BufferSize, (uint)(bitrate / 2));
                    SetCodec(LowLatency, 1, VarBool);
                    SetCodec(QualityVsSpeed, 33);
                    break;
                case RateMode.Careful:
                    SetCodec(RateControl, 2); // unconstrained VBR
                    SetCodec(MeanBitRate, (uint)bitrate);
                    SetCodec(QualityVsSpeed, 100);
                    break;
                default:
                    SetCodec(RateControl, 1); // peak-constrained VBR
                    SetCodec(MeanBitRate, (uint)bitrate);
                    SetCodec(MaxBitRate, (uint)(bitrate * 2L));
                    SetCodec(QualityVsSpeed, 50);
                    break;
            }
            try { SequenceHeader = _mft.GetOutputCurrentType(0).GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); } catch { }

            var format = tenBit ? Format.P010 : Format.NV12;
            _pool = new ID3D11Texture2D[choice.Hardware ? 8 : 2];
            for (var i = 0; i < _pool.Length; i++)
                _pool[i] = gpu.Texture(width, height, format, BindFlags.RenderTarget | BindFlags.ShaderResource);

            _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
            if (choice.Hardware)
            {
                _events = _mft.QueryInterface<IMFMediaEventGenerator>();
                _pump = new Thread(Pump) { IsBackground = true, Name = "Exo Capture encoder", Priority = ThreadPriority.AboveNormal };
                _pump.Start();
            }
        }
        catch
        {
            _mft.Dispose();
            throw;
        }
    }

    private static void Colour(IMFMediaType type, bool hdr)
    {
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, hdr ? 9u : 2u); // BT.2020 or BT.709
        type.Set(MediaTypeAttributeKeys.TransferFunction, hdr ? 15u : 5u); // PQ (2084) or BT.709
        type.Set(MediaTypeAttributeKeys.YuvMatrix, hdr ? 4u : 1u);
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u); // 16-235
    }

    /// <summary>The texture the next frame goes into (the video processor's output); it is the encoder's until it has passed.</summary>
    internal ID3D11Texture2D NextInput() => _pool[_next];

    /// <summary>Sends the frame in <see cref="NextInput"/> to the encoder. False when the encoder is too busy to take it now.</summary>
    internal bool Encode(long time, long duration, bool key)
    {
        if (_closed || Failed is not null) return false;
        var texture = _pool[_next];
        if (Choice.Hardware)
        {
            if (!_credits.Wait(key ? 200 : 30)) return false;
            _next = (_next + 1) % _pool.Length;
        }
        IMFSample sample;
        if (Choice.Hardware)
        {
            sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
            using (var two = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = two.ContiguousLength;
            sample.AddBuffer(buffer);
        }
        else sample = SystemSample(texture);
        using (sample)
        {
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            if (key) SetCodec(ForceKeyFrame, 1);
            try { _mft.ProcessInput(0, sample, 0); }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                if (++_failures > 30) Failed = $"Encoder refused a frame (0x{ex.HResult:X8})";
                return false;
            }
        }
        _failures = 0;
        if (!Choice.Hardware) DrainSync();
        return true;
    }

    /// <summary>A new average rate from the next frame (Go Live, when Discord's server asks for less or more).</summary>
    internal void SetBitrate(int bitrate)
    {
        Bitrate = bitrate;
        SetCodec(MeanBitRate, (uint)bitrate);
        SetCodec(BufferSize, (uint)(bitrate / 2));
    }

    // ---------------------------------------------------------------- the processor's encoder

    private IMFSample SystemSample(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        _staging ??= _gpu.Device.CreateTexture2D(new Texture2DDescription(desc.Format, desc.Width, desc.Height, 1, 1, BindFlags.None,
            ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        _gpu.Context.CopyResource(_staging, texture);
        var mapped = _gpu.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var lumaBytes = Width * Height;
            var total = lumaBytes * 3 / 2;
            var buffer = MediaFactory.MFCreateMemoryBuffer(total);
            buffer.Lock(out var dest, out _, out _);
            var src = (byte*)mapped.DataPointer;
            for (var y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + y * mapped.RowPitch, (byte*)dest + y * Width, Width, Width);
            var chroma = src + mapped.RowPitch * desc.Height;
            for (var y = 0; y < Height / 2; y++)
                Buffer.MemoryCopy(chroma + y * mapped.RowPitch, (byte*)dest + lumaBytes + y * Width, Width, Width);
            buffer.Unlock();
            buffer.CurrentLength = total;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            buffer.Dispose();
            return sample;
        }
        finally { _gpu.Context.Unmap(_staging, 0); }
    }

    private void DrainSync()
    {
        while (true)
        {
            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Width * Height * 2);
            sample.AddBuffer(buffer);
            var data = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
            data.Events?.Dispose();
            if (hr.Code == unchecked((int)0xC00D6D72)) return; // MF_E_TRANSFORM_NEED_MORE_INPUT
            if (hr.Code == unchecked((int)0xC00D6D61)) { Renegotiate(); continue; } // MF_E_TRANSFORM_STREAM_CHANGE
            if (hr.Failure) return;
            Deliver(sample);
        }
    }

    // ---------------------------------------------------------------- the card's encoder

    private void Pump()
    {
        try
        {
            while (!_closed || !_drained.IsSet)
            {
                IMFMediaEvent e;
                try { e = _events!.GetEvent(0); }
                catch (SharpGen.Runtime.SharpGenException) { break; }
                using (e)
                {
                    switch ((int)e.EventType)
                    {
                        case 601: // METransformNeedInput
                            try { _credits.Release(); } catch (SemaphoreFullException) { }
                            break;
                        case 602: // METransformHaveOutput
                            Output();
                            break;
                        case 606: // METransformDrainComplete
                            _drained.Set();
                            return;
                    }
                }
            }
        }
        catch (Exception ex) { Failed = "Encoder output: " + ex.Message; }
        finally { _drained.Set(); }
    }

    private void Output()
    {
        var data = new OutputDataBuffer { StreamID = 0 };
        var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
        data.Events?.Dispose();
        if (hr.Code == unchecked((int)0xC00D6D61)) { Renegotiate(); return; }
        if (hr.Failure || data.Sample is null) return;
        using (data.Sample) Deliver(data.Sample);
    }

    private void Renegotiate()
    {
        using var type = _mft.GetOutputAvailableType(0, 0);
        _mft.SetOutputType(0, type, 0);
        try { SequenceHeader = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); } catch { }
    }

    private void Deliver(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        buffer.Unlock();
        var key = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success && clean != 0;
        if (SequenceHeader is null && key)
            try { using var t = _mft.GetOutputCurrentType(0); SequenceHeader = t.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); } catch { }
        _output(bytes, sample.SampleTime, key);
    }

    // ---------------------------------------------------------------- ICodecAPI

    private const ushort VarUi4 = 19, VarBool = 11;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public short Bool;
    }

    private void SetCodec(Guid key, uint value, ushort type = VarUi4)
    {
        if (Marshal.QueryInterface(_mft.NativePointer, ICodecApi, out var api) != 0) return;
        try
        {
            var v = new Variant { Type = type };
            if (type == VarBool) v.Bool = value != 0 ? (short)-1 : (short)0; else v.UInt = value;
            var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)(*(void***)api)[9];
            setValue(api, &key, &v);
        }
        finally { Marshal.Release(api); }
    }

    /// <summary>Waits for what is still in the encoder to come out, then lets it go.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _mft.ProcessMessage(TMessageType.MessageCommandDrain, 0);
            if (Choice.Hardware) _drained.Wait(2000);
            else DrainSync();
        }
        catch (SharpGen.Runtime.SharpGenException) { }
        if (Marshal.QueryInterface(_mft.NativePointer, IMFShutdown, out var shutdown) == 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)shutdown)[3])(shutdown);
            Marshal.Release(shutdown);
        }
        _pump?.Join(2000);
        _events?.Dispose();
        foreach (var t in _pool) t.Dispose();
        _staging?.Dispose();
        _mft.Dispose();
        _credits.Dispose();
    }
}
