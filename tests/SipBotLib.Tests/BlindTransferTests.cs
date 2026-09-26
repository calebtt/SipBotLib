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
    private readonly TaskCompletionSource<bool> _calleeHungUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _callerEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _acceptTransfer = true;

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
        _callee.OnCallHungup += _ => _calleeHungUp.TrySetResult(true);
        _callee.OnTransferRequested += (_, _) => _acceptTransfer;

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
        Assert.True(await Within(_calleeHungUp.Task, TimeSpan.FromSeconds(5)), "the transferee never received a BYE");
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
