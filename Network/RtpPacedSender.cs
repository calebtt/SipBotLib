using Serilog;
using SipBot;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

/// <summary>
/// Built for paced sending of 8kHz 16bit mono PCMU encoded audio.
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

    public Action<uint, byte[]>? SendAction 
    {
        get {  return _sendAction; }
        set 
        {
            _sendAction = value;
        }
    }

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
        _senderTask = Task.Run(async () => { await RunAsync(_cts.Token); });
    }

    public async Task Stop()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            // Due to a potential bug with the ConcurrentQueue, we will avoid clearing an empty CQ.
            _queue.Enqueue(_silenceFrame);
            _queue.Clear(); // Flush the queue to remove all pending frames

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

            SendAction = null;
        }
    }

    public void ResetBuffer()
    {
        if (_senderTask == null || _senderTask.IsCompleted)
        {
            Log.Warning($"[{nameof(RtpPacedSender)}] Cannot reset buffer: sender is not running.");
            return;
        }

        // Enqueue a silence frame to avoid potential ConcurrentQueue empty clear bug
        _queue.Enqueue(_silenceFrame);
        _queue.Clear();
        Log.Information($"[{nameof(RtpPacedSender)}] Buffer reset, queue cleared.");
    }

    private async Task RunAsync(CancellationToken token)
    {
        Stopwatch stopwatch = new();
        stopwatch.Restart();
        long expectedElapsedMs = 0;

        while (!token.IsCancellationRequested)
        {
            if (SendAction != null)
            {
                byte[] frameToSend = _queue.TryDequeue(out var frame) ? frame : _silenceFrame;
                if(SendAction is not null)
                {
                    SendAction(_twentyMsByteCount, frameToSend);
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
        if (pcmuFrame?.Length == _twentyMsByteCount)
        {
            _queue.Enqueue(pcmuFrame);
        }
        else
        {
            Log.Warning($"PCMU frame enqueued to RTP sender is not {_twentyMsByteCount} bytes.");
        }
    }

}