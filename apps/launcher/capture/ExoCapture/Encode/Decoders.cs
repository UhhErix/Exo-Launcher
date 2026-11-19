using System.Runtime.InteropServices;
using ExoCapture.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ExoCapture.Encode;

/// <summary>A decoded picture: the decoder's texture (and its array slice) and when it shows, in 100 ns.</summary>
internal readonly record struct Decoded(ID3D11Texture2D Texture, uint Slice, long Time);

/// <summary>
/// A clip's video decoded by Windows (Media Foundation's source reader with the graphics card's decoder), into NV12 or
/// P010 textures on Exo Capture's device: what exports, shrinking, joins, thumbnails and GIFs read.
/// </summary>
internal sealed class VideoDecoder : IDisposable
{
    private readonly IMFSourceReader _reader;
    private ID3D11Texture2D? _current;
    internal readonly int Width, Height;
    internal readonly bool TenBit;
    internal readonly double Fps;
    internal readonly long DurationTicks;

    internal VideoDecoder(GpuDevice gpu, string path)
    {
        using var attributes = MediaFactory.MFCreateAttributes(4);
        attributes.Set(SourceReaderAttributeKeys.D3DManager, gpu.Manager);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 0u);
        _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using var native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
        MediaFactory.MFGetAttributeSize(native, MediaTypeAttributeKeys.FrameSize, out var w, out var h);
        (Width, Height) = ((int)w, (int)h);
        TenBit = native.GetUInt32(MediaTypeAttributeKeys.TransferFunction, out var transfer).Success && transfer == 15;
        Fps = MediaFactory.MFGetAttributeRatio(native, MediaTypeAttributeKeys.FrameRate, out var num, out var den).Success && den > 0 ? num / (double)den : 60;
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, TenBit ? VideoFormatGuids.P010 : VideoFormatGuids.NV12);
        _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        try
        {
            var duration = _reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            DurationTicks = duration.Value is ulong d ? (long)d : duration.Value is long l ? l : 0;
        }
        catch (SharpGen.Runtime.SharpGenException) { }
    }

    internal void Seek(long ticks) => _reader.SetCurrentPosition(Math.Max(0, ticks));

    /// <summary>The next picture, or null at the end. Valid until the next call.</summary>
    internal Decoded? Next()
    {
        while (true)
        {
            using var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) return null;
            if ((flags & SourceReaderFlag.Error) != 0) throw new CaptureException("decode the video");
            if (sample is null) continue;
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgi is null) throw new CaptureException("decode the video (the decoder did not use the graphics card)");
            var pointer = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
            _current?.Dispose();
            _current = new ID3D11Texture2D(pointer);
            return new Decoded(_current, dxgi.SubresourceIndex, time);
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _reader.Dispose();
    }
}

/// <summary>Raw AAC frames to 48 kHz stereo 16-bit PCM and back: Windows' own AAC decoder and encoder (Media Foundation).</summary>
internal sealed class AacCodec : IDisposable
{
    private static readonly Guid AacEncoderClsid = new("93AF0C51-2275-45d2-A35B-F2BA21CAED00");
    private static readonly Guid AacDecoderClsid = new("32D186A7-218F-4C75-8876-DD77273A8999");
    private readonly IMFTransform _mft;
    private readonly bool _encoder;
    internal const int Rate = 48000, Channels = 2, FrameSamples = 1024;

    internal AacCodec(bool encoder, int bitrate = 160_000)
    {
        _encoder = encoder;
        _mft = Create(encoder ? AacEncoderClsid : AacDecoderClsid);
        using var pcm = MediaFactory.MFCreateMediaType();
        pcm.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        pcm.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        pcm.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        pcm.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Rate);
        pcm.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Channels);
        pcm.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
        pcm.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Rate * 4));
        using var aac = MediaFactory.MFCreateMediaType();
        aac.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        aac.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        aac.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        aac.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Rate);
        aac.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Channels);
        aac.Set(MediaTypeAttributeKeys.AacPayloadType, 0u); // raw
        aac.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
        if (encoder)
        {
            aac.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(bitrate / 8));
            _mft.SetInputType(0, pcm, 0);
            _mft.SetOutputType(0, aac, 0);
        }
        else
        {
            // The decoder's input type carries the HEAACWAVEINFO tail: payload type 0, then the AudioSpecificConfig.
            byte[] user = [0, 0, 0x29, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x11, 0x90];
            aac.SetBlob(MediaTypeAttributeKeys.UserData, user);
            _mft.SetInputType(0, aac, 0);
            _mft.SetOutputType(0, pcm, 0);
        }
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    private static IMFTransform Create(Guid clsid)
    {
        var iid = typeof(IMFTransform).GUID;
        var hr = CoCreateInstance(clsid, 0, 1 /* INPROC */, iid, out var pointer);
        if (hr < 0) throw new CaptureException($"AAC {(clsid == AacEncoderClsid ? "encoder" : "decoder")} (0x{hr:X8})");
        return new IMFTransform(pointer);
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint pointer);

    /// <summary>Feeds one input (1024 stereo samples to encode, or one AAC frame to decode) and gives what comes out.</summary>
    internal List<byte[]> Process(ReadOnlySpan<byte> input)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(input.Length);
        buffer.Lock(out var pointer, out _, out _);
        unsafe { input.CopyTo(new Span<byte>((void*)pointer, input.Length)); }
        buffer.Unlock();
        buffer.CurrentLength = input.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = 0;
        sample.SampleDuration = 213333;
        _mft.ProcessInput(0, sample, 0);
        return Drain();
    }

    internal List<byte[]> Flush()
    {
        try { _mft.ProcessMessage(TMessageType.MessageCommandDrain, 0); } catch (SharpGen.Runtime.SharpGenException) { }
        return Drain();
    }

    private List<byte[]> Drain()
    {
        var list = new List<byte[]>();
        while (true)
        {
            using var output = MediaFactory.MFCreateSample();
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_encoder ? 8192 : FrameSamples * 4 * 4);
            output.AddBuffer(outBuffer);
            var data = new OutputDataBuffer { StreamID = 0, Sample = output };
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
            data.Events?.Dispose();
            if (hr.Failure) break;
            using var contiguous = output.ConvertToContiguousBuffer();
            contiguous.Lock(out var p, out _, out var length);
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            contiguous.Unlock();
            if (length > 0) list.Add(bytes);
        }
        return list;
    }

    public void Dispose() => _mft.Dispose();
}
