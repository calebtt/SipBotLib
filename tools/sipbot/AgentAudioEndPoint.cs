using NAudio.Wave;
using Serilog;
using SipBot;

namespace SipBot.Cli;

/// <summary>
/// Headless media endpoint: silence keep-alive, WAV playback via PCM frames, inbound WAV record.
/// </summary>
sealed class AgentAudioEndPoint : BaseAudioEndPoint
{
    private readonly object _recLock = new();
    private WaveFileWriter? _writer;
    private int _recordRate;
    private DateTime _recordUntilUtc = DateTime.MaxValue;
    private TaskCompletionSource<string?>? _recordDone;
    private string? _recordPath;

    public AgentAudioEndPoint(bool keepAlive, bool wideband)
        : base(enableContinuousKeepAlive: keepAlive, enableWidebandAudio: wideband)
    {
    }

    public override Task InitializeAsync() => Task.CompletedTask;

    public override Task ShutdownAsync()
    {
        StopRecording();
        return Task.CompletedTask;
    }

    public void PlayWav(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("play requires 'file' (a readable PCM or μ-law WAV). " + AudioCaps.PlayHelp);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"WAV file not found: {path}. {AudioCaps.PlayHelp}",
                path);
        }

        byte[] wav;
        try
        {
            wav = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(AudioCaps.UnreadableWavMessage(path, ex.Message), ex);
        }

        try
        {
            using var ms = new MemoryStream(wav, writable: false);
            using var reader = new WaveFileReader(ms);
            _ = reader.WaveFormat.SampleRate;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(AudioCaps.UnreadableWavMessage(path, ex.Message), ex);
        }

        byte[] pcm = AudioAlgos.ConvertWavToPcm(wav, 8000);
        if (pcm.Length == 0)
            throw new InvalidOperationException(AudioCaps.UnreadableWavMessage(path, "decode produced no PCM."));

        // 8 kHz PCM in; SendAudioFrame resamples again to the negotiated clock (8 or 16 kHz).
        PlayPcm(pcm, 8000);
    }

    public void PlayPcm(byte[] pcm, int sampleRateHz)
    {
        int frameSamples = Math.Max(1, sampleRateHz / 50);
        int frameBytes = frameSamples * 2;
        for (int off = 0; off + frameBytes <= pcm.Length; off += frameBytes)
        {
            var chunk = new byte[frameBytes];
            Buffer.BlockCopy(pcm, off, chunk, 0, frameBytes);
            SendAudioFrame(chunk, sampleRateHz);
        }
    }

    public Task<string?> StartRecordingAsync(string path, int seconds)
    {
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "seconds must be > 0");

        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        lock (_recLock)
        {
            StopRecordingLocked();
            _recordRate = 8000;
            _recordPath = full;
            _recordUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            _writer = new WaveFileWriter(full, new WaveFormat(_recordRate, 16, 1));
            _recordDone = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Log.Information("Recording inbound PCM to {Path} for {Seconds}s", full, seconds);
            return _recordDone.Task;
        }
    }

    public void StopRecording()
    {
        lock (_recLock)
            StopRecordingLocked();
    }

    private void StopRecordingLocked()
    {
        if (_writer == null)
            return;

        try
        {
            _writer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error closing WAV recorder");
        }

        _writer = null;
        var done = _recordDone;
        var path = _recordPath;
        _recordDone = null;
        _recordPath = null;
        done?.TrySetResult(path);
    }

    protected override Task ProcessAudioAsync(byte[] pcm, int sampleRateHz)
    {
        lock (_recLock)
        {
            if (_writer == null)
                return Task.CompletedTask;

            if (DateTime.UtcNow >= _recordUntilUtc)
            {
                StopRecordingLocked();
                return Task.CompletedTask;
            }

            byte[] toWrite = pcm;
            if (sampleRateHz != _recordRate)
                toWrite = AudioAlgos.ResamplePcmWithNAudio(pcm, sampleRateHz, _recordRate);

            if (toWrite.Length > 0)
                _writer.Write(toWrite, 0, toWrite.Length);
        }

        return Task.CompletedTask;
    }
}
