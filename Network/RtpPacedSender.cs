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

    // Count of enqueued real (non-silence) frames not yet sent. Drives IsPlaying and
    // SendingComplete without inspecting post-filter bytes or scanning the queue.
    private int _pendingRealFrameCount;

    /// <summary>Raised once the last queued real (non-silence) frame has been sent.</summary>
    public event Action? SendingComplete;

    public Action<uint, byte[]>? SendAction
    {
        get => _sendAction;
        set => _sendAction = value;
    }

    /// <summary>
    /// True while at least one real (non-silence) frame is still queued or currently being sent.
    /// Silence keep-alive frames and an empty queue both report false.
    /// </summary>
    public bool IsPlaying => Volatile.Read(ref _pendingRealFrameCount) > 0;

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

        SignalSendingCompleteIfPending();

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

        SignalSendingCompleteIfPending();

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
                bool dequeuedFromQueue = _queue.TryDequeue(out var frame);
                if (!dequeuedFromQueue)
                {
                    frame = _silenceFrame;
                }

                // Classify real vs silence on the pre-filter frame. Filters (e.g. volume ducking)
                // must not change whether this frame counts toward IsPlaying / SendingComplete,
                // and silence enqueued after real audio must not block completion forever.
                bool wasRealAudio = dequeuedFromQueue && frame != null && !IsSilenceFrame(frame);

                var frameToSend = frame!;

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

                if (wasRealAudio)
                {
                    // Last real frame has actually gone out when the counter hits zero.
                    if (Interlocked.Decrement(ref _pendingRealFrameCount) == 0)
                    {
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
        if (pcmuFrame == null || pcmuFrame.Length == 0)
            return;

        // Accept bulk PCMU and split into 20ms frames (160 bytes). Callers sometimes pass
        // a whole Grok/OpenAI audio delta encoded at once.
        if (pcmuFrame.Length != _twentyMsByteCount)
        {
            if (pcmuFrame.Length % _twentyMsByteCount == 0)
            {
                for (int i = 0; i < pcmuFrame.Length; i += (int)_twentyMsByteCount)
                {
                    var slice = new byte[_twentyMsByteCount];
                    Buffer.BlockCopy(pcmuFrame, i, slice, 0, (int)_twentyMsByteCount);
                    EnqueueFrame(slice);
                }
                return;
            }

            // Partial trailing frame: pad with silence so we don't drop the last samples
            if (pcmuFrame.Length < _twentyMsByteCount)
            {
                var padded = new byte[_twentyMsByteCount];
                Buffer.BlockCopy(pcmuFrame, 0, padded, 0, pcmuFrame.Length);
                for (int i = pcmuFrame.Length; i < padded.Length; i++)
                    padded[i] = _silenceFrameByte;
                EnqueueFrame(padded);
                return;
            }

            Log.Warning(
                "PCMU buffer length {Len} is not a multiple of {Frame}; dropping remainder after full frames.",
                pcmuFrame.Length, _twentyMsByteCount);
            int full = pcmuFrame.Length / (int)_twentyMsByteCount;
            for (int i = 0; i < full; i++)
            {
                var slice = new byte[_twentyMsByteCount];
                Buffer.BlockCopy(pcmuFrame, i * (int)_twentyMsByteCount, slice, 0, (int)_twentyMsByteCount);
                EnqueueFrame(slice);
            }
            return;
        }

        EnqueueFrame(pcmuFrame);
    }

    private void EnqueueFrame(byte[] pcmuFrame)
    {
        if (!IsSilenceFrame(pcmuFrame))
        {
            Interlocked.Increment(ref _pendingRealFrameCount);
        }

        _queue.Enqueue(pcmuFrame);
    }

    private bool IsSilenceFrame(byte[] frame) =>
        frame.AsSpan().SequenceEqual(_silenceFrame.AsSpan());

    /// <summary>
    /// Drops any outstanding real-frame count (queue is about to be wiped) and fires
    /// <see cref="SendingComplete"/> if something was still pending.
    /// </summary>
    private void SignalSendingCompleteIfPending()
    {
        int pending = Interlocked.Exchange(ref _pendingRealFrameCount, 0);
        if (pending > 0)
        {
            SendingComplete?.Invoke();
        }
    }
}
