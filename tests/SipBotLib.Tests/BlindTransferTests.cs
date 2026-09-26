using System.Diagnostics;
using System.Net;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using SipBot;
using Xunit;

namespace SipBotLib.Tests;

/// <summary>
/// SipClient calls a plain SIPUserAgent over loopback and blind-transfers the call. When the
/// REFER is accepted the original leg must end (BYE + CallEnded), as BlindTransferAsync documents.
/// When it is declined the call stays up.
/// </summary>
public class BlindTransferTests : IDisposable
{
    private sealed class SilentEndPoint : BaseAudioEndPoint
    {
        public override Task InitializeAsync() => Task.CompletedTask;
        public override Task ShutdownAsync() => Task.CompletedTask;
        protected override Task ProcessAudioAsync(byte[] pcm, int sampleRateHz) => Task.CompletedTask;
    }

    // Nothing listens here; the transferee's call to it just fails later.
    private const string TransferTarget = "sip:c@127.0.0.1:9";

    private readonly SIPTransport _callerTransport = new();
    private readonly SIPTransport _calleeTransport = new();
    private readonly SipClient _caller;
    private readonly SIPUserAgent _callee;
    private readonly int _calleePort;
    private readonly TaskCompletionSource<bool> _byeAtCallee = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _callerEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _acceptTransfer = true;
    private bool _sendFinalNotify;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TaskCompletionSource<(SIPResponseStatusCodesEnum Status, TimeSpan At)> _notifyAnswered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _byeAt;

    public BlindTransferTests()
    {
        _callerTransport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        var calleeChannel = new SIPUDPChannel(IPAddress.Loopback, 0);
        _calleeTransport.AddSIPChannel(calleeChannel);
        _calleePort = calleeChannel.Port;

        _callee = new SIPUserAgent(_calleeTransport, null);
        _callee.OnIncomingCall += async (ua, req) =>
        {
            var uas = ua.AcceptCall(req);
            var media = new VoIPMediaSession(new MediaEndPoints { AudioSource = new AudioExtrasSource() })
            {
                AcceptRtpFromAny = true
            };
            await ua.Answer(uas, media);
        };
        // Watch for the BYE at the transport. After accepting the REFER, SIPUserAgent starts its
        // own call to the transfer target, which can replace its dialog before the BYE arrives,
        // so OnCallHungup is not a reliable signal here.
        _calleeTransport.SIPTransportRequestReceived += (_, _, req) =>
        {
            if (req.Method == SIPMethodsEnum.BYE && _byeAtCallee.TrySetResult(true))
                _byeAt = _clock.Elapsed;
            return Task.CompletedTask;
        };
        _callee.OnTransferRequested += (_, _) =>
        {
            if (_acceptTransfer && _sendFinalNotify)
                _ = SendFinalReferNotifyAsync();
            return _acceptTransfer;
        };

        var config = new SipConfig { Server = "127.0.0.1", Username = "101", Password = "x" };
        _caller = new SipClient(_callerTransport, config, enableAutoReconnection: false, enableHealthMonitoring: false);
        _caller.CallEnded += _ => _callerEnded.TrySetResult(true);
    }

    public void Dispose()
    {
        _caller.Dispose();
        if (_callee.IsCallActive)
            _callee.Hangup();
        _calleeTransport.Shutdown();
    }

    /// <summary>
    /// What Asterisk does after accepting a REFER: an in-dialog NOTIFY (Event: refer) whose
    /// sipfrag reports the outcome. SIPSorcery's own transferee does not send one.
    /// </summary>
    private async Task SendFinalReferNotifyAsync()
    {
        await Task.Delay(50); // let the 202 go out first
        var notify = _callee.Dialogue.GetInDialogRequest(SIPMethodsEnum.NOTIFY);
        notify.Header.Event = "refer";
        notify.Header.SubscriptionState = "terminated;reason=noresource";
        notify.Header.ContentType = "message/sipfrag;version=2.0";
        notify.Body = "SIP/2.0 200 OK\r\n";
        var tx = new SIPNonInviteTransaction(_calleeTransport, notify, null);
        tx.NonInviteTransactionFinalResponseReceived += (_, _, _, resp) =>
        {
            _notifyAnswered.TrySetResult((resp.Status, _clock.Elapsed));
            return Task.FromResult(System.Net.Sockets.SocketError.Success);
        };
        tx.SendRequest();
    }

    private static async Task<bool> Within(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private async Task ConnectAsync()
    {
        var audio = new SilentEndPoint();
        bool answered = await _caller.CallAsync($"sip:b@127.0.0.1:{_calleePort}", audio, audio, ringTimeoutSeconds: 10);
        Assert.True(answered, $"call not answered: {_caller.LastOutboundFailure}");
        Assert.True(_caller.IsCallActive);
    }

    [Fact]
    public async Task Accepted_transfer_hangs_up_the_original_leg()
    {
        await ConnectAsync();

        bool transferred = await _caller.BlindTransferAsync(TransferTarget, TimeSpan.FromSeconds(5));

        Assert.True(transferred);
        Assert.True(await Within(_callerEnded.Task, TimeSpan.FromSeconds(5)), "CallEnded was not raised after the transfer");
        Assert.False(_caller.IsCallActive);
        Assert.True(await Within(_byeAtCallee.Task, TimeSpan.FromSeconds(5)), "the transferee never received a BYE");
    }

    [Fact]
    public async Task Final_progress_NOTIFY_is_answered_before_the_BYE()
    {
        _sendFinalNotify = true;
        await ConnectAsync();

        var elapsed = Stopwatch.StartNew();
        bool transferred = await _caller.BlindTransferAsync(TransferTarget, TimeSpan.FromSeconds(5));
        elapsed.Stop();

        Assert.True(transferred);
        Assert.True(await Within(_notifyAnswered.Task, TimeSpan.FromSeconds(5)), "the NOTIFY got no final response");
        var (status, answeredAt) = await _notifyAnswered.Task;
        Assert.Equal(SIPResponseStatusCodesEnum.Ok, status);
        Assert.True(await Within(_byeAtCallee.Task, TimeSpan.FromSeconds(5)), "the transferee never received a BYE");
        Assert.True(_byeAt >= answeredAt, $"BYE at {_byeAt} came before the NOTIFY was answered at {answeredAt}");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"BlindTransferAsync took {elapsed.Elapsed}; it should not wait out the cap");
        Assert.True(_callerEnded.Task.IsCompleted);
    }

    [Fact]
    public async Task Declined_transfer_leaves_the_call_up()
    {
        _acceptTransfer = false;
        await ConnectAsync();

        bool transferred = await _caller.BlindTransferAsync(TransferTarget, TimeSpan.FromSeconds(5));
        await Task.Delay(500);

        Assert.False(transferred);
        Assert.True(_caller.IsCallActive);
        Assert.False(_callerEnded.Task.IsCompleted);
    }
}
