using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Channels;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace VoiceBoothKiosk.Audio.Windows;

internal sealed class PooledAudioPacket : IDisposable
{
    private byte[]? buffer;

    private PooledAudioPacket(byte[] buffer, int length)
    {
        this.buffer = buffer;
        Length = length;
    }

    public byte[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(PooledAudioPacket));

    public int Length { get; }

    public static PooledAudioPacket CopyFrom(byte[] source, int length)
    {
        var rented = ArrayPool<byte>.Shared.Rent(length);
        source.AsSpan(0, length).CopyTo(rented);
        return new PooledAudioPacket(rented, length);
    }

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref buffer, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// Converts queued WASAPI packets into mono floating-point samples. Reads intentionally block
/// on a dedicated conversion worker; the WASAPI callback only copies and enqueues packets.
/// </summary>
internal sealed class QueuedMonoSampleProvider : ISampleProvider, IDisposable
{
    private readonly ChannelReader<PooledAudioPacket> reader;
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly WaveFormat sourceFormat;
    private readonly SampleEncoding encoding;
    private readonly int bytesPerContainer;
    private PooledAudioPacket? currentPacket;
    private int currentOffset;
    private bool disposed;

    public QueuedMonoSampleProvider(ChannelReader<PooledAudioPacket> reader, WaveFormat sourceFormat)
    {
        this.reader = reader;
        this.sourceFormat = sourceFormat;

        if (sourceFormat.SampleRate <= 0 || sourceFormat.Channels <= 0 || sourceFormat.BlockAlign <= 0)
        {
            throw new UnsupportedCaptureFormatException($"The input device reported an invalid mix format: {sourceFormat}.");
        }

        if (sourceFormat.BlockAlign % sourceFormat.Channels != 0)
        {
            throw new UnsupportedCaptureFormatException($"The input device's block alignment is unsupported: {sourceFormat}.");
        }

        bytesPerContainer = sourceFormat.BlockAlign / sourceFormat.Channels;
        encoding = DetermineEncoding(sourceFormat, bytesPerContainer);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The requested sample range is outside the destination buffer.");
        }

        var written = 0;
        while (written < count)
        {
            if (currentPacket is null || currentOffset >= currentPacket.Length)
            {
                currentPacket?.Dispose();
                currentPacket = null;
                currentOffset = 0;

                if (!TryTakeNextPacket(out currentPacket))
                {
                    break;
                }

                var packet = currentPacket ?? throw new InvalidOperationException("The audio packet queue returned no packet.");
                if (packet.Length % sourceFormat.BlockAlign != 0)
                {
                    throw new InvalidDataException(
                        $"The input device supplied {packet.Length} bytes, which is not aligned to its {sourceFormat.BlockAlign}-byte sample frame.");
                }
            }

            var activePacket = currentPacket ?? throw new InvalidOperationException("The current audio packet was unexpectedly unavailable.");
            var availableFrames = (activePacket.Length - currentOffset) / sourceFormat.BlockAlign;
            var framesToRead = Math.Min(availableFrames, count - written);
            var packetBuffer = activePacket.Buffer;

            for (var frame = 0; frame < framesToRead; frame++)
            {
                var frameOffset = currentOffset + (frame * sourceFormat.BlockAlign);
                double sum = 0;
                for (var channel = 0; channel < sourceFormat.Channels; channel++)
                {
                    var sampleOffset = frameOffset + (channel * bytesPerContainer);
                    sum += DecodeSample(packetBuffer.AsSpan(sampleOffset, bytesPerContainer));
                }

                var sample = (float)(sum / sourceFormat.Channels);
                buffer[offset + written + frame] = float.IsFinite(sample)
                    ? Math.Clamp(sample, -1f, 1f)
                    : 0f;
            }

            currentOffset += framesToRead * sourceFormat.BlockAlign;
            written += framesToRead;
        }

        return written;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposalCancellation.Cancel();
        currentPacket?.Dispose();
        currentPacket = null;

        while (reader.TryRead(out var packet))
        {
            packet.Dispose();
        }

        disposalCancellation.Dispose();
    }

    private bool TryTakeNextPacket(out PooledAudioPacket? packet)
    {
        while (true)
        {
            if (reader.TryRead(out packet))
            {
                return true;
            }

            bool canRead;
            try
            {
                canRead = reader.WaitToReadAsync(disposalCancellation.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (disposed)
            {
                packet = null;
                return false;
            }

            if (!canRead)
            {
                packet = null;
                return false;
            }
        }
    }

    private static SampleEncoding DetermineEncoding(WaveFormat format, int bytesPerContainer)
    {
        var waveEncoding = format.Encoding;
        Guid? extensibleSubFormat = null;
        if (format is WaveFormatExtensible extensible)
        {
            extensibleSubFormat = extensible.SubFormat;
        }

        var isFloat = waveEncoding == WaveFormatEncoding.IeeeFloat ||
                      extensibleSubFormat == AudioSubtypes.MFAudioFormat_Float;
        var isPcm = waveEncoding == WaveFormatEncoding.Pcm ||
                    extensibleSubFormat == AudioSubtypes.MFAudioFormat_PCM;

        if (isFloat && bytesPerContainer == 4)
        {
            return SampleEncoding.Float32;
        }

        if (isFloat && bytesPerContainer == 8)
        {
            return SampleEncoding.Float64;
        }

        if (isPcm)
        {
            return bytesPerContainer switch
            {
                1 => SampleEncoding.Pcm8,
                2 => SampleEncoding.Pcm16,
                3 => SampleEncoding.Pcm24,
                4 => SampleEncoding.Pcm32,
                _ => throw Unsupported(format),
            };
        }

        throw Unsupported(format);
    }

    private static UnsupportedCaptureFormatException Unsupported(WaveFormat format) =>
        new($"The input device mix format is not supported: {format}. PCM and IEEE-float WASAPI formats are supported.");

    private double DecodeSample(ReadOnlySpan<byte> bytes) => encoding switch
    {
        SampleEncoding.Pcm8 => (bytes[0] - 128) / 128d,
        SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768d,
        SampleEncoding.Pcm24 => DecodePcm24(bytes) / 8_388_608d,
        SampleEncoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648d,
        SampleEncoding.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        SampleEncoding.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
        _ => 0,
    };

    private static int DecodePcm24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
    }

    private enum SampleEncoding
    {
        Pcm8,
        Pcm16,
        Pcm24,
        Pcm32,
        Float32,
        Float64,
    }
}
