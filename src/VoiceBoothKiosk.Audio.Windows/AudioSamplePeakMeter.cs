using System.Buffers.Binary;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>Calculates a normalized absolute peak directly from interleaved WASAPI packets.</summary>
internal sealed class AudioSamplePeakMeter
{
    private readonly SampleEncoding encoding;
    private readonly int bytesPerContainer;

    public AudioSamplePeakMeter(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels <= 0 || format.BlockAlign <= 0 || format.BlockAlign % format.Channels != 0)
        {
            throw new UnsupportedCaptureFormatException($"The input device reported an invalid mix format: {format}.");
        }

        bytesPerContainer = format.BlockAlign / format.Channels;
        encoding = DetermineEncoding(format, bytesPerContainer);
    }

    public float Calculate(byte[] buffer, int bytesRecorded)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var usableBytes = Math.Min(buffer.Length, Math.Max(0, bytesRecorded));
        usableBytes -= usableBytes % bytesPerContainer;
        var data = buffer.AsSpan(0, usableBytes);
        var peak = 0d;

        for (var offset = 0; offset < data.Length; offset += bytesPerContainer)
        {
            var sample = DecodeSample(data.Slice(offset, bytesPerContainer));
            if (!double.IsFinite(sample))
            {
                continue;
            }

            peak = Math.Max(peak, Math.Abs(sample));
        }

        return (float)Math.Clamp(peak, 0, 1);
    }

    private static SampleEncoding DetermineEncoding(WaveFormat format, int bytesPerContainer)
    {
        Guid? extensibleSubFormat = format is WaveFormatExtensible extensible ? extensible.SubFormat : null;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      extensibleSubFormat == AudioSubtypes.MFAudioFormat_Float;
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
                    extensibleSubFormat == AudioSubtypes.MFAudioFormat_PCM;

        if (isFloat)
        {
            return bytesPerContainer switch
            {
                4 => SampleEncoding.Float32,
                8 => SampleEncoding.Float64,
                _ => throw Unsupported(format),
            };
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

    private static UnsupportedCaptureFormatException Unsupported(WaveFormat format) =>
        new($"The input device mix format is not supported: {format}. PCM and IEEE-float WASAPI formats are supported.");

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
