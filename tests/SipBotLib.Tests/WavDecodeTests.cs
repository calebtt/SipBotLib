using NAudio.Codecs;
using NAudio.Wave;
using SipBot;
using SipBot.Cli;
using Xunit;

namespace SipBotLib.Tests;

/// <summary>
/// WAV decoding accepts G.711 (mu-law, A-law) files as well as PCM: NAudio's ToSampleProvider()
/// alone throws "Unsupported source encoding" on them, which made `sipbot play` reject mu-law WAVs.
/// </summary>
public class WavDecodeTests : IDisposable
{
    private const int Rate = 8000;
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files)
            File.Delete(f);
    }

    /// <summary>One second of a 440 Hz tone as 16-bit samples.</summary>
    private static short[] Tone()
    {
        var samples = new short[Rate];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        return samples;
    }

    private static byte[] Wav(WaveFormat format, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
            writer.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] MuLawWav(short[] samples) =>
        Wav(WaveFormat.CreateMuLawFormat(Rate, 1), samples.Select(MuLawEncoder.LinearToMuLawSample).ToArray());

    private static byte[] ALawWav(short[] samples) =>
        Wav(WaveFormat.CreateALawFormat(Rate, 1), samples.Select(ALawEncoder.LinearToALawSample).ToArray());

    private static short[] Samples(byte[] pcm)
    {
        var s = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, s, 0, s.Length * 2);
        return s;
    }

    private string TempFile(byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sipbotlib-wav-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, contents);
        _files.Add(path);
        return path;
    }

    public static TheoryData<string> G711Encodings() => new() { "mulaw", "alaw" };

    private static (byte[] Wav, short[] Expected) G711(string encoding)
    {
        var tone = Tone();
        return encoding == "mulaw"
            ? (MuLawWav(tone), tone.Select(MuLawEncoder.LinearToMuLawSample).Select(MuLawDecoder.MuLawToLinearSample).ToArray())
            : (ALawWav(tone), tone.Select(ALawEncoder.LinearToALawSample).Select(ALawDecoder.ALawToLinearSample).ToArray());
    }

    [Theory]
    [MemberData(nameof(G711Encodings))]
    public void ConvertWavToPcm_decodes_G711(string encoding)
    {
        var (wav, expected) = G711(encoding);

        var pcm = Samples(AudioAlgos.ConvertWavToPcm(wav, Rate));

        Assert.Equal(expected.Length, pcm.Length);
        // Float round trip through the sample provider can move a sample by one step.
        Assert.All(expected.Zip(pcm), p => Assert.InRange(p.Second - p.First, -1, 1));
    }

    [Theory]
    [MemberData(nameof(G711Encodings))]
    public void ConvertWavToPcm_resamples_G711(string encoding)
    {
        var (wav, _) = G711(encoding);

        var pcm = AudioAlgos.ConvertWavToPcm(wav, 16000);

        Assert.InRange(pcm.Length / 2, 2 * Rate - 64, 2 * Rate + 64);
    }

    [Fact]
    public void ConvertWavToPcm_still_reads_PCM()
    {
        var tone = Tone();
        var data = new byte[tone.Length * 2];
        Buffer.BlockCopy(tone, 0, data, 0, data.Length);

        var pcm = Samples(AudioAlgos.ConvertWavToPcm(Wav(new WaveFormat(Rate, 16, 1), data), Rate));

        Assert.Equal(tone.Length, pcm.Length);
        Assert.All(tone.Zip(pcm), p => Assert.InRange(p.Second - p.First, -1, 1));
    }

    [Fact]
    public void ReadWelcomeWavBytesAsPcmu_reads_a_mulaw_file()
    {
        var tone = Tone();
        var mulaw = tone.Select(MuLawEncoder.LinearToMuLawSample).ToArray();

        var pcmu = AudioAlgos.ReadWelcomeWavBytesAsPcmu(TempFile(MuLawWav(tone)));

        Assert.Equal(mulaw.Length, pcmu.Length);
        // Re-encoding a decoded sample can land on the neighboring code.
        Assert.All(mulaw.Zip(pcmu), p => Assert.InRange(
            MuLawDecoder.MuLawToLinearSample(p.Second) - MuLawDecoder.MuLawToLinearSample(p.First), -300, 300));
    }

    [Fact]
    public void ReadWelcomeWavBytesAsPcm16kHz_reads_an_alaw_file()
    {
        var pcm = AudioAlgos.ReadWelcomeWavBytesAsPcm16kHz(TempFile(ALawWav(Tone())));

        Assert.InRange(pcm.Length / 2, 2 * Rate - 64, 2 * Rate + 64);
    }

    [Fact]
    public void PlayWav_accepts_a_mulaw_file()
    {
        var endpoint = new AgentAudioEndPoint(keepAlive: false, wideband: false);

        // Before the fix this threw "… is not a readable WAV … decode produced no PCM."
        var ex = Record.Exception(() => endpoint.PlayWav(TempFile(MuLawWav(Tone()))));

        Assert.Null(ex);
    }
}
