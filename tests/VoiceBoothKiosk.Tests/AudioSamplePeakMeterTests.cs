using NAudio.Wave;
using VoiceBoothKiosk.Audio.Windows;

namespace VoiceBoothKiosk.Tests;

public sealed class AudioSamplePeakMeterTests
{
    [Fact]
    public void Calculates_peak_from_interleaved_pcm16_samples()
    {
        var samples = new short[] { 0, -16_384, 8_192, 4_096 };
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var meter = new AudioSamplePeakMeter(new WaveFormat(48_000, 16, 2));

        var peak = meter.Calculate(bytes, bytes.Length);

        Assert.Equal(0.5f, peak, precision: 4);
    }

    [Fact]
    public void Calculates_peak_from_float_samples_and_ignores_non_finite_values()
    {
        var samples = new[] { 0.1f, -0.75f, float.NaN, 0.25f };
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var meter = new AudioSamplePeakMeter(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1));

        var peak = meter.Calculate(bytes, bytes.Length);

        Assert.Equal(0.75f, peak, precision: 4);
    }

    [Fact]
    public void Sign_extends_pcm24_and_ignores_an_incomplete_trailing_sample()
    {
        byte[] bytes =
        [
            0x00, 0x00, 0xC0, // -0.5 in signed 24-bit PCM
            0x00, 0x00, 0x20, // +0.25
            0xFF,              // incomplete sample must be ignored
        ];
        var meter = new AudioSamplePeakMeter(new WaveFormat(48_000, 24, 1));

        var peak = meter.Calculate(bytes, bytes.Length);

        Assert.Equal(0.5f, peak, precision: 4);
    }
}
