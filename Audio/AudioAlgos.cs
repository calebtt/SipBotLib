using NAudio.Codecs;
using NAudio.Dsp;
using NAudio.Wave;
using Serilog;

namespace SipBot;
public static class AudioAlgos
{
    /// <summary>
    /// Normalizes 16-bit PCM audio to a specified maximum amplitude to prevent clipping.
    /// </summary>
    public static byte[] NormalizePcmAudio(byte[] pcmAudio, float maxAmplitude = 0.9f)
    {
        // Handle odd-length input by padding
        if (pcmAudio.Length % 2 != 0)
        {
            Log.Warning($"Invalid PCM audio length: {pcmAudio.Length} bytes (must be even for 16-bit), padding with 1 byte");
            byte[] paddedAudio = new byte[pcmAudio.Length + 1];
            Array.Copy(pcmAudio, paddedAudio, pcmAudio.Length);
            pcmAudio = paddedAudio;
        }

        short[] samples = new short[pcmAudio.Length / 2];
        Buffer.BlockCopy(pcmAudio, 0, samples, 0, pcmAudio.Length);

        float maxSample = samples.Max(s => Math.Abs((float)s));
        if (maxSample == 0)
        {
            Log.Debug("No normalization needed: max sample amplitude is zero");
            return pcmAudio;
        }

        float scale = (maxAmplitude * short.MaxValue) / maxSample;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(samples[i] * scale, short.MinValue, short.MaxValue);
        }

        byte[] normalized = new byte[pcmAudio.Length];
        Buffer.BlockCopy(samples, 0, normalized, 0, pcmAudio.Length);
        Log.Debug($"Normalized PCM audio: scale factor {scale:F2}, max amplitude {maxAmplitude}");
        return normalized;
    }

    /// <summary>
    /// Resamples PCM audio to the target sample rate, handling variable input sizes.
    /// </summary>
    public static byte[] ResamplePcmWithNAudio(byte[] inputPcm, int inputSampleRate, int outputSampleRate)
    {
        // Handle odd-length input by padding
        if (inputPcm.Length % 2 != 0)
        {
            Log.Warning($"Invalid input PCM length: {inputPcm.Length} bytes (must be even for 16-bit), padding with 1 byte");
            byte[] paddedInput = new byte[inputPcm.Length + 1];
            Array.Copy(inputPcm, paddedInput, inputPcm.Length);
            inputPcm = paddedInput;
        }

        // Calculate expected output size
        int inputSamples = inputPcm.Length / 2; // 16-bit mono
        double sampleRateRatio = (double)outputSampleRate / inputSampleRate;
        int expectedOutputSamples = (int)Math.Ceiling(inputSamples * sampleRateRatio);
        // Ensure even output samples for 16-bit audio
        if (expectedOutputSamples % 2 != 0)
            expectedOutputSamples++;

        using var inputStream = new MemoryStream(inputPcm);
        using var rawSource = new RawSourceWaveStream(inputStream, new WaveFormat(inputSampleRate, 16, 1));
        var outFormat = new WaveFormat(outputSampleRate, 16, 1);

        using var conversionStream = new WaveFormatConversionStream(outFormat, rawSource);
        using var outputStream = new MemoryStream();
        byte[] buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = conversionStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            outputStream.Write(buffer, 0, bytesRead);
        }

        byte[] resampled = outputStream.ToArray();
        Log.Debug($"Resampled audio from {inputSampleRate}Hz to {outputSampleRate}Hz: input {inputPcm.Length} bytes, output {resampled.Length} bytes");

        // Ensure output is even-length and padded to expected size
        if (resampled.Length % 2 != 0)
        {
            byte[] paddedOutput = new byte[resampled.Length + 1];
            Array.Copy(resampled, paddedOutput, resampled.Length);
            resampled = paddedOutput;
        }

        if (resampled.Length < expectedOutputSamples * 2)
        {
            byte[] paddedOutput = new byte[expectedOutputSamples * 2];
            Array.Copy(resampled, paddedOutput, resampled.Length);
            Log.Debug($"Padded resampled audio from {resampled.Length} to {paddedOutput.Length} bytes to match expected samples");
            resampled = paddedOutput;
        }
        else if (resampled.Length > expectedOutputSamples * 2)
        {
            byte[] trimmedOutput = new byte[expectedOutputSamples * 2];
            Array.Copy(resampled, trimmedOutput, trimmedOutput.Length);
            Log.Debug($"Trimmed resampled audio from {resampled.Length} to {trimmedOutput.Length} bytes to match expected samples");
            resampled = trimmedOutput;
        }

        return resampled;
    }

    /// <summary>
    /// Convert a WAV file (as byte array) to PCMU (G.711 μ-law) encoded byte array at 8kHz mono.
    /// Note that the WAV header is used for the wav data's sample rate info.
    /// </summary>
    /// <param name="wavAudio">Full WAV file data</param>
    /// <returns>PCMU-encoded byte array</returns>
    public static byte[] ConvertWavToPcmu(byte[] wavAudio)
    {
        return EncodePcmToPcmuWithNAudio(ConvertWavToPcm(wavAudio, 8000));
    }

    /// <summary>
    /// Convert a WAV file (as byte array) to raw PCM (16-bit, mono) at a specified target sample rate.
    /// Note that it reads the WAV file header for information on the source audio's sample rate.
    /// </summary>
    /// <param name="wavAudio">WAV file data</param>
    /// <param name="targetSampleRateHz">Desired output sample rate (e.g. 8000, 16000)</param>
    /// <returns>Raw PCM byte array (16-bit mono)</returns>
    public static byte[] ConvertWavToPcm(byte[] wavAudio, int targetSampleRateHz)
    {
        try
        {
            using var wavStream = new MemoryStream(wavAudio);
            using var reader = new WaveFileReader(wavStream);

            var inputFormat = reader.WaveFormat;
            var targetFormat = new WaveFormat(targetSampleRateHz, 16, 1);

            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60
            };

            using var outStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                outStream.Write(buffer, 0, bytesRead);
            }

            byte[] rawPcm = outStream.ToArray();
            Log.Debug($"Converted WAV to PCM: {rawPcm.Length} bytes, {targetSampleRateHz} Hz, 16-bit mono");
            return rawPcm;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert WAV to raw PCM.");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Encodes 16 bit mono PCM data, irrespective of sample rate (this encoding algo doesn't depend on it.)
    /// </summary>
    public static byte[] EncodePcmToPcmuWithNAudio(byte[] pcmSamples)
    {
        if (pcmSamples.Length % 2 != 0)
        {
            Log.Warning($"[EncodePcmToPcmuWithNAudio] PCM samples length {pcmSamples.Length} is not even, trimming last byte.");
            Array.Resize(ref pcmSamples, pcmSamples.Length - 1);
        }

        int sampleCount = pcmSamples.Length / 2;
        byte[] pcmu = new byte[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // Combine two bytes into one short (little-endian)
            short sample = (short)(pcmSamples[i * 2] | (pcmSamples[i * 2 + 1] << 8));
            pcmu[i] = MuLawEncoder.LinearToMuLawSample(sample);
        }

        return pcmu;
    }

    /// <summary>
    /// Generates durationMs worth of 16 bit mono PCM audio at sampleRate hz.
    /// </summary>
    public static byte[] GenerateSilencePCMWithNAudio(int sampleRate, int durationMs)
    {
        int samples = sampleRate * durationMs / 1000;
        byte[] silence = new byte[samples * 2]; // 16-bit = 2 bytes/sample
        return silence;
    }

    /// <summary>
    /// Converts MP3 audio to 8kHz 16-bit mono PCMU encoded.
    /// </summary>
    /// <param name="mp3Stream">MemoryStream containing MP3 data</param>
    /// <returns>PCMU-encoded byte array</returns>
    public static byte[] ConvertMp3ToPcmu(MemoryStream mp3Stream)
    {
        if (!mp3Stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable.");
        }

        try
        {
            // Convert MP3 to WAV first
            using var mp3Reader = new Mp3FileReader(mp3Stream);
            using var wavStream = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(wavStream, mp3Reader);
            wavStream.Position = 0;

            // Use existing AudioAlgos functions to convert WAV to PCMU
            byte[] wavData = wavStream.ToArray();
            byte[] pcmData = ConvertWavToPcm(wavData, 8000);
            byte[] pcmuData = EncodePcmToPcmuWithNAudio(pcmData);

            Log.Debug($"Converted MP3 to PCMU: {pcmuData.Length} bytes");
            return pcmuData;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert MP3 to PCMU.");
            return Array.Empty<byte>();
        }
    }

    public static byte[]? ReadWelcomeFileMp3BytesAsPcmu(string fileName)
    {
        if (File.Exists(fileName))
        {
            var fileBytes = File.ReadAllBytes(fileName);
            return ConvertMp3ToPcmu(new(fileBytes));
        }
        Log.Information($"No welcome message mp3 file bytes could be read for: {fileName}");
        return null;
    }

    /// <summary>
    /// Converts PCMU (G.711 μ-law) encoded audio to 16-bit 16kHz mono PCM.
    /// </summary>
    /// <param name="pcmuAudio">PCMU-encoded byte array</param>
    /// <returns>16-bit 16kHz mono PCM byte array</returns>
    public static byte[] ConvertPcmuToPcm16kHz(byte[] pcmuAudio)
    {
        try
        {
            // Step 1: Decode PCMU to 16-bit PCM at 8kHz
            int sampleCount = pcmuAudio.Length;
            byte[] pcm8kHz = new byte[sampleCount * 2]; // 16-bit = 2 bytes per sample

            for (int i = 0; i < sampleCount; i++)
            {
                // Decode mu-law sample to 16-bit linear PCM
                short linearSample = MuLawDecoder.MuLawToLinearSample(pcmuAudio[i]);
                // Write as little-endian 16-bit sample
                pcm8kHz[i * 2] = (byte)(linearSample & 0xFF);
                pcm8kHz[i * 2 + 1] = (byte)(linearSample >> 8);
            }

            // Step 2: Resample from 8kHz to 16kHz
            byte[] pcm16kHz = ResamplePcmWithNAudio(pcm8kHz, 8000, 16000);

            Log.Debug($"Converted PCMU to 16-bit 16kHz PCM: {pcm16kHz.Length} bytes");
            return pcm16kHz;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert PCMU to 16-bit 16kHz PCM.");
            return Array.Empty<byte>();
        }
    }
}