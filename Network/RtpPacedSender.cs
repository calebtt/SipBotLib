using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SipBot;

/// <summary>
/// Built for paced sending of 8kHz 16bit mono PCMU encoded audio.
/// Supports temporary audio filtering via ApplyFilter (manual clear via ClearFilter).
/// </summary>
public class RtpPacedSender
{
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private CancellationTokenSource? _cts = null;
    private Task? _senderTask = null;
    private readonly byte[] _silenceFrame;
    private const int _frameDurationMs = 20; // ms of audio
    private const uint _twentyMsByteCount = 160; // Bytes for 20ms of audio
    private const byte _silenceFrameByte = (byte)0x7F; // Silence frame data byte

    private Action<uint, byte[]>? _sendAction;

    // Filter support: volatile for safe reads in the hot send loop.
    private volatile Func<byte[], byte[]>? _currentFilter;

    // Tracks whether real (non-silence) audio is currently queued/being sent, so SendingComplete
    // can fire exactly once the last real frame has actually gone out.
    private volatile bool _hasAudioPending = false;

    /// <summary>Raised once the last queued real (non-silence) frame has been sent.</summary>
    public event Action? SendingComplete;

    public Action<uint, byte[]>? SendAction
    {
        get => _sendAction;
        set => _sendAction = value;
    }

    /// <summary>
    /// Gets a value indicating whether real audio frames (e.g., from TTS) are queued to send.
    /// False if only silence is being sent.
    /// </summary>
    public bool IsPlaying => !_queue.IsEmpty;

    public RtpPacedSender()
    {
        _silenceFrame = Enumerable.Repeat(_silenceFrameByte, (int)_twentyMsByteCount).ToArray();
    }

    public void Start()
    {
        if (_senderTask != null && !_senderTask.IsCompleted)
        {
            throw new InvalidOperationException("The sender is already started.");
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _senderTask = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    public async Task Stop()
    {
        if (_cts == null) return;

        _cts.Cancel();

        // Due to a potential bug with the ConcurrentQueue, we will avoid clearing an empty CQ.
        if (!_queue.IsEmpty)
        {
            _queue.Enqueue(_silenceFrame);
        }
        while (_queue.TryDequeue(out _)) { }

        if (_hasAudioPending)
        {
            Volatile.Write(ref _hasAudioPending, false);
            SendingComplete?.Invoke();
        }

        if (_senderTask != null)
        {
            try
            {
                await _senderTask;
            }
            catch (TaskCanceledException)
            {
            }
            _senderTask = null;
        }

        _sendAction = null;
        ClearFilter();
        _cts.Dispose();
        _cts = null;
    }

    public void ResetBuffer()
    {
        if (_senderTask == null || _senderTask.IsCompleted)
        {
            Log.Warning($"[{nameof(RtpPacedSender)}] Cannot reset buffer: sender is not running.");
            return;
        }

        if (_hasAudioPending)
        {
            Volatile.Write(ref _hasAudioPending, false);
            SendingComplete?.Invoke();
        }

        // Enqueue a silence frame to avoid potential ConcurrentQueue empty clear bug
        if (!_queue.IsEmpty)
        {
            _queue.Enqueue(_silenceFrame);
        }
        while (_queue.TryDequeue(out _)) { }

        Log.Information($"[{nameof(RtpPacedSender)}] Buffer reset, queue cleared.");
    }

    /// <summary>
    /// Applies a filter to outgoing audio frames until ClearFilter() is called.
    /// Replaces any existing filter.
    /// </summary>
    /// <param name="filter">The filter function to apply (input: 160-byte frame; output: transformed frame).</param>
    public void ApplyFilter(Func<byte[], byte[]> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        _currentFilter = filter;

        Log.Information($"[{nameof(RtpPacedSender)}] Applied audio filter (clear manually via ClearFilter).");
    }

    /// <summary>
    /// Immediately clears any active filter.
    /// </summary>
    public void ClearFilter()
    {
        _currentFilter = null;
        Log.Debug($"[{nameof(RtpPacedSender)}] Audio filter cleared.");
    }

    private async Task RunAsync(CancellationToken token)
    {
        Stopwatch stopwatch = new();
        stopwatch.Restart();
        long expectedElapsedMs = 0;

        while (!token.IsCancellationRequested)
        {
            if (_sendAction != null)
            {
                if (!_queue.TryDequeue(out var frame))
                {
                    frame = _silenceFrame;
                }

                var frameToSend = frame;

                var filter = _currentFilter;
                if (filter != null)
                {
                    try
                    {
                        frameToSend = filter(frameToSend);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"[{nameof(RtpPacedSender)}] Error applying audio filter; sending unfiltered frame.");
                    }
                }

                _sendAction(_twentyMsByteCount, frameToSend);

                // Fire SendingComplete once the last real (non-silence) frame has actually gone out.
                if (_hasAudioPending)
                {
                    var isSilence = frameToSend.AsSpan().SequenceEqual(_silenceFrame.AsSpan());
                    if (!isSilence && _queue.IsEmpty)
                    {
                        Volatile.Write(ref _hasAudioPending, false);
                        SendingComplete?.Invoke();
                        Log.Debug($"[{nameof(RtpPacedSender)}] Sending complete: all real audio frames sent.");
                    }
                }

                expectedElapsedMs += _frameDurationMs;
            }

            var actualElapsed = stopwatch.ElapsedMilliseconds;
            var delay = expectedElapsedMs - actualElapsed;

            if (delay > 0)
            {
                await Task.Delay((int)delay, token);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    public void Enqueue(byte[] pcmuFrame)
    {
        if (pcmuFrame == null || pcmuFrame.Length != _twentyMsByteCount)
        {
            Log.Warning($"PCMU frame enqueued to RTP sender is not {_twentyMsByteCount} bytes.");
            return;
        }

        var isSilence = pcmuFrame.AsSpan().SequenceEqual(_silenceFrame.AsSpan());
        if (!isSilence)
        {
            Volatile.Write(ref _hasAudioPending, true);
        }

        _queue.Enqueue(pcmuFrame);
    }
}
