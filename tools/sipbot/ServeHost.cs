using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using SipBot;
using SIPSorcery.SIP;

namespace SipBot.Cli;

sealed class ServeHost : IDisposable
{
    private readonly ServeOptions _options;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly object _dtmfLock = new();
    private readonly List<char> _dtmfBuffer = new();
    private TaskCompletionSource<DtmfWaitResult>? _dtmfWait;
    private int _dtmfMaxDigits;
    private SIPRequest? _pendingInvite;
    private AgentAudioEndPoint _endpoint;
    private SipClient _client = null!;
    private SIPTransport _transport = null!;
    private DateTime _callStartedUtc;
    private volatile bool _stopping;
    private CancellationTokenSource _runCts = null!;

    public ServeHost(ServeOptions options)
    {
        _options = options;
        _endpoint = NewEndpoint();
    }

    public async Task RunAsync(CancellationTokenSource runCts)
    {
        _runCts = runCts;
        var cancellationToken = runCts.Token;
        var cfg = SipBotSettings.LoadActiveConfig(_options.SettingsPath, _options.ConfigIndex);
        int localPort = SipBotSettings.GetLocalBindPort(0);

        Log.Information(
            "sipbot serve user={User} server={Server} localPort={Port} autoAnswer={Auto} keepAlive={KA} wideband={WB}",
            cfg.Username, cfg.Server, localPort, _options.AutoAnswer, !_options.NoKeepAlive, !_options.PcmuOnly);

        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Any, localPort));

        _client = new SipClient(_transport, cfg, registrationExpirySeconds: 120);
        WireClient(_client);
        _client.StartRegistration();

        var stdinTask = Task.Run(() => ReadStdinLoopAsync(cancellationToken), cancellationToken);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stopping = true;
        try
        {
            if (_client.IsCallActive)
                _client.Hangup();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Hangup during shutdown");
        }

        try
        {
            await stdinTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private AgentAudioEndPoint NewEndpoint() =>
        new(!_options.NoKeepAlive, !_options.PcmuOnly);

    private void WireClient(SipClient client)
    {
        client.StatusMessage += (_, msg) => Log.Information("[status] {Msg}", msg);
        client.ErrorOccurred += (_, ex) =>
        {
            Log.Error(ex, "[error]");
            Jsonl.Error(ex.Message);
        };

        client.RegistrationStatusChanged += c =>
        {
            Log.Information("[reg] registered={R}", c.IsRegistered);
            if (c.IsRegistered)
            {
                Jsonl.Event("registered", new()
                {
                    ["user"] = c.Username,
                    ["server"] = c.Server,
                    ["audio"] = AudioCaps.Advertised(_options.PcmuOnly)
                });
            }
            EmitStatus();
        };

        client.IncomingCall += (c, invite) =>
        {
            _ = HandleInviteAsync(c, invite);
        };

        client.CallAnswer += _ =>
        {
            _callStartedUtc = DateTime.UtcNow;
            Jsonl.Event("answered", new()
            {
                ["audio"] = AudioCaps.Negotiated(_endpoint.NegotiatedSendFormat)
            });
        };

        client.CallEnded += _ =>
        {
            var duration = _callStartedUtc == default
                ? 0
                : (int)Math.Round((DateTime.UtcNow - _callStartedUtc).TotalSeconds);
            _callStartedUtc = default;
            _pendingInvite = null;
            CompleteDtmfWait(timedOut: true);
            _endpoint.StopRecording();
            Jsonl.Event("ended", new() { ["durationSec"] = duration });
            try { _callGate.Release(); } catch (SemaphoreFullException) { }
        };

        client.CallDurationUpdated += (_, d) =>
            Log.Information("[call] duration {D:mm\\:ss}", d);

        client.DtmfReceived += (_, tone, duration) =>
        {
            char digit = SipUriNormalizer.DtmfToneToChar(tone);
            Jsonl.Event("dtmf", new() { ["digit"] = digit.ToString() });
            lock (_dtmfLock)
            {
                if (_dtmfWait == null)
                    return;
                _dtmfBuffer.Add(digit);
                if (_dtmfBuffer.Count >= _dtmfMaxDigits)
                    CompleteDtmfWaitLocked(timedOut: false);
            }
        };

        client.TransferSucceeded += _ =>
            Log.Information("Blind transfer succeeded");
        client.TransferFailed += (_, msg) =>
        {
            Log.Warning("Blind transfer failed: {Msg}", msg);
            Jsonl.Error(msg);
        };
    }

    private async Task HandleInviteAsync(SipClient client, SIPRequest invite)
    {
        Jsonl.Event("invite", new()
        {
            ["from"] = invite.Header.From?.ToString() ?? "",
            ["callId"] = invite.Header.CallId ?? ""
        });

        if (!_options.AutoAnswer)
        {
            _pendingInvite = invite;
            return;
        }

        await AnswerInternalAsync(client, invite, _options.AutoAnswerPlay).ConfigureAwait(false);
    }

    private async Task AnswerInternalAsync(SipClient client, SIPRequest invite, string? playPath)
    {
        if (!await _callGate.WaitAsync(0).ConfigureAwait(false))
        {
            Jsonl.Error("already handling a call");
            return;
        }

        bool answered = false;
        try
        {
            if (client.IsCallActive)
            {
                Jsonl.Error("call already active");
                return;
            }

            _endpoint.StopRecording();
            _endpoint = NewEndpoint();
            client.Accept(invite);
            _pendingInvite = null;
            answered = await client.Answer(_endpoint, _endpoint).ConfigureAwait(false);
            if (!answered)
            {
                Jsonl.Error("Answer() failed");
                return;
            }

            if (!string.IsNullOrWhiteSpace(playPath))
            {
                try
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    _endpoint.PlayWav(playPath);
                }
                catch (Exception ex)
                {
                    Jsonl.Error($"play failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "failed to answer");
            Jsonl.Error(ex.Message);
        }
        finally
        {
            if (!answered)
            {
                try { _callGate.Release(); } catch (SemaphoreFullException) { }
            }
        }
    }

    private async Task ReadStdinLoopAsync(CancellationToken cancellationToken)
    {
        var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        while (!cancellationToken.IsCancellationRequested && !_stopping)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line == null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var cmd = AgentCommand.Parse(line);
                await DispatchAsync(cmd, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "command failed");
                Jsonl.Error(ex.Message);
            }
        }
    }

    private async Task DispatchAsync(AgentCommand cmd, CancellationToken cancellationToken)
    {
        switch (cmd.Cmd)
        {
            case "status":
                EmitStatus();
                break;
            case "answer":
                await CmdAnswerAsync(cmd).ConfigureAwait(false);
                break;
            case "hangup":
                if (_client.IsCallActive)
                    _client.Hangup();
                else
                    Jsonl.Error("no active call");
                break;
            case "transfer":
                await CmdTransferAsync(cmd).ConfigureAwait(false);
                break;
            case "play":
                CmdPlay(cmd);
                break;
            case "record":
                _ = CmdRecordAsync(cmd);
                break;
            case "wait_dtmf":
                await CmdWaitDtmfAsync(cmd, cancellationToken).ConfigureAwait(false);
                break;
            case "dial":
                await CmdDialAsync(cmd, cancellationToken).ConfigureAwait(false);
                break;
            case "quit":
                Jsonl.Event("status", StatusFields());
                _runCts.Cancel();
                break;
            default:
                Jsonl.Error($"unknown cmd '{cmd.Cmd}'");
                break;
        }
    }

    private async Task CmdAnswerAsync(AgentCommand cmd)
    {
        var invite = _pendingInvite;
        if (invite == null)
        {
            Jsonl.Error("no pending invite");
            return;
        }
        await AnswerInternalAsync(_client, invite, cmd.Play).ConfigureAwait(false);
    }

    private async Task CmdTransferAsync(AgentCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Target))
        {
            Jsonl.Error("transfer requires 'target'");
            return;
        }
        if (!_client.IsCallActive)
        {
            Jsonl.Error("no active call");
            return;
        }

        string uri = SipUriNormalizer.Normalize(cmd.Target, _client.Server);
        bool ok = await _client.BlindTransferAsync(uri).ConfigureAwait(false);
        if (!ok)
            Jsonl.Error($"transfer to {uri} failed");
    }

    private void CmdPlay(AgentCommand cmd)
    {
        string? path = cmd.File ?? cmd.Play;
        if (string.IsNullOrWhiteSpace(path))
        {
            Jsonl.Error("play requires 'file'");
            return;
        }
        if (!_client.IsCallActive)
        {
            Jsonl.Error("no active call");
            return;
        }
        try
        {
            _endpoint.PlayWav(path);
        }
        catch (Exception ex)
        {
            Jsonl.Error(ex.Message);
        }
    }

    private async Task CmdRecordAsync(AgentCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.File))
        {
            Jsonl.Error("record requires 'file'");
            return;
        }
        if (!_client.IsCallActive)
        {
            Jsonl.Error("no active call");
            return;
        }

        int seconds = cmd.Seconds ?? 30;
        try
        {
            var path = await _endpoint.StartRecordingAsync(cmd.File, seconds).ConfigureAwait(false);
            Jsonl.Event("recorded", new()
            {
                ["file"] = path ?? cmd.File,
                ["seconds"] = seconds
            });
        }
        catch (Exception ex)
        {
            Jsonl.Error(ex.Message);
        }
    }

    private async Task CmdWaitDtmfAsync(AgentCommand cmd, CancellationToken cancellationToken)
    {
        int timeout = cmd.TimeoutSec ?? 30;
        int maxDigits = Math.Max(1, cmd.MaxDigits ?? 1);
        var tcs = new TaskCompletionSource<DtmfWaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_dtmfLock)
        {
            _dtmfBuffer.Clear();
            _dtmfMaxDigits = maxDigits;
            _dtmfWait = tcs;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var reg = timeoutCts.Token.Register(() => CompleteDtmfWait(timedOut: true));

        DtmfWaitResult result;
        try
        {
            result = await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Jsonl.Error(ex.Message);
            return;
        }

        Jsonl.Event("dtmf_result", new()
        {
            ["digits"] = result.Digits,
            ["timedOut"] = result.TimedOut
        });
    }

    private async Task CmdDialAsync(AgentCommand cmd, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cmd.Uri))
        {
            Jsonl.Error("dial requires 'uri'");
            return;
        }
        if (!await _callGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Jsonl.Error("already handling a call");
            return;
        }

        bool connected = false;
        try
        {
            _endpoint.StopRecording();
            _endpoint = NewEndpoint();
            string uri = SipUriNormalizer.Normalize(cmd.Uri, _client.Server);
            connected = await _client.CallAsync(uri, _endpoint, _endpoint, ringTimeoutSeconds: 60, cancellationToken)
                .ConfigureAwait(false);
            if (!connected)
            {
                string? reason = _client.LastOutboundFailure;
                Jsonl.Error(string.IsNullOrEmpty(reason)
                    ? $"dial failed: {uri}"
                    : $"dial failed: {uri} ({reason})");
                return;
            }

            if (!string.IsNullOrWhiteSpace(cmd.Play))
            {
                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    _endpoint.PlayWav(cmd.Play);
                }
                catch (Exception ex)
                {
                    Jsonl.Error($"play failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Jsonl.Error(ex.Message);
        }
        finally
        {
            if (!connected)
            {
                try { _callGate.Release(); } catch (SemaphoreFullException) { }
            }
        }
    }

    private void CompleteDtmfWait(bool timedOut)
    {
        lock (_dtmfLock)
            CompleteDtmfWaitLocked(timedOut);
    }

    private void CompleteDtmfWaitLocked(bool timedOut)
    {
        var wait = _dtmfWait;
        if (wait == null)
            return;
        _dtmfWait = null;
        string digits = new(_dtmfBuffer.ToArray());
        _dtmfBuffer.Clear();
        bool timed = timedOut && digits.Length < _dtmfMaxDigits;
        wait.TrySetResult(new DtmfWaitResult(digits, timed));
    }

    private void EmitStatus() => Jsonl.Event("status", StatusFields());

    private Dictionary<string, object?> StatusFields()
    {
        bool callActive = _client?.IsCallActive ?? false;
        return new()
        {
            ["registered"] = _client?.IsRegistered ?? false,
            ["callActive"] = callActive,
            ["audio"] = AudioCaps.ForStatus(
                _options.PcmuOnly,
                callActive,
                callActive ? _endpoint.NegotiatedSendFormat : null)
        };
    }

    public void Dispose()
    {
        try { _endpoint.StopRecording(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _transport?.Shutdown(); } catch { /* ignore */ }
        _callGate.Dispose();
    }

    readonly record struct DtmfWaitResult(string Digits, bool TimedOut);
}

sealed class ServeOptions
{
    public string? SettingsPath { get; init; }
    public int ConfigIndex { get; init; }
    public bool AutoAnswer { get; init; }
    public string? AutoAnswerPlay { get; init; }
    public bool PcmuOnly { get; init; }
    public bool NoKeepAlive { get; init; }
}

static class Shutdown
{
    // Must be rooted: disposing/GC of PosixSignalRegistration unregisters the handler.
    private static PosixSignalRegistration? _sigterm;
    private static PosixSignalRegistration? _sigint;

    public static CancellationTokenSource CreateCts()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            TryCancel(cts);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCancel(cts);

        try
        {
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                TryCancel(cts);
            });
            _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
            {
                ctx.Cancel = true;
                TryCancel(cts);
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "POSIX signal registration unavailable");
        }

        return cts;
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
