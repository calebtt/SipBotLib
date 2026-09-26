using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.SIP;
using SipBot;
using Xunit;

namespace SipBotLib.Tests;

/// <summary>
/// SipClient.CallRinging fires once per outbound call, on the first 180 or 183, with that status;
/// never for other provisional responses, never twice, and not at all without a ringing response.
/// A scripted callee on loopback answers each INVITE with the given provisional responses and then
/// 486, so no media is needed.
/// </summary>
public class CallRingingEventTests : IDisposable
{
    private sealed class SilentEndPoint : BaseAudioEndPoint
    {
        public override Task InitializeAsync() => Task.CompletedTask;
        public override Task ShutdownAsync() => Task.CompletedTask;
        protected override Task ProcessAudioAsync(byte[] pcm, int sampleRateHz) => Task.CompletedTask;
    }

    private readonly SIPTransport _callerTransport = new();
    private readonly SIPTransport _calleeTransport = new();
    private readonly SipClient _caller;
    private readonly int _calleePort;
    private readonly ConcurrentQueue<int> _ringing = new();
    private readonly ConcurrentQueue<string> _status = new();
    private volatile SIPResponseStatusCodesEnum[] _script = Array.Empty<SIPResponseStatusCodesEnum>();

    public CallRingingEventTests()
    {
        _callerTransport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        var calleeChannel = new SIPUDPChannel(IPAddress.Loopback, 0);
        _calleeTransport.AddSIPChannel(calleeChannel);
        _calleePort = calleeChannel.Port;
        _calleeTransport.SIPTransportRequestReceived += OnCalleeRequest;

        var config = new SipConfig { Server = "127.0.0.1", Username = "101", Password = "x" };
        _caller = new SipClient(_callerTransport, config, enableAutoReconnection: false, enableHealthMonitoring: false);
        _caller.CallRinging += (_, status) => _ringing.Enqueue(status);
        _caller.StatusMessage += (_, msg) => _status.Enqueue(msg);
    }

    public void Dispose()
    {
        _caller.Dispose();
        _calleeTransport.Shutdown();
    }

    private Task OnCalleeRequest(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest req)
    {
        if (req.Method != SIPMethodsEnum.INVITE)
            return Task.CompletedTask;

        var script = _script;
        _ = Task.Run(async () =>
        {
            var tx = new UASInviteTransaction(_calleeTransport, req, null);
            foreach (var status in script)
            {
                await tx.SendProvisionalResponse(SIPResponse.GetResponse(req, status, null));
                await Task.Delay(100);
            }
            tx.SendFinalResponse(SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null));
        });
        return Task.CompletedTask;
    }

    private async Task PlaceCallAsync(params SIPResponseStatusCodesEnum[] provisional)
    {
        _script = provisional;
        var audio = new SilentEndPoint();
        bool answered = await _caller.CallAsync($"sip:b@127.0.0.1:{_calleePort}", audio, audio, ringTimeoutSeconds: 10);
        Assert.False(answered);
        Assert.StartsWith("486", _caller.LastOutboundFailure);
        await Task.Delay(200); // let any late event arrive before asserting on it
    }

    public static TheoryData<SIPResponseStatusCodesEnum[], int[]> Scripts() => new()
    {
        { new[] { SIPResponseStatusCodesEnum.Ringing }, new[] { 180 } },
        { new[] { SIPResponseStatusCodesEnum.SessionProgress }, new[] { 183 } },
        { new[] { SIPResponseStatusCodesEnum.Ringing, SIPResponseStatusCodesEnum.Ringing }, new[] { 180 } },
        { new[] { SIPResponseStatusCodesEnum.SessionProgress, SIPResponseStatusCodesEnum.Ringing }, new[] { 183 } },
        { new[] { SIPResponseStatusCodesEnum.CallIsBeingForwarded }, Array.Empty<int>() },
        { Array.Empty<SIPResponseStatusCodesEnum>(), Array.Empty<int>() },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Fires_once_on_the_first_180_or_183(SIPResponseStatusCodesEnum[] provisional, int[] expected)
    {
        await PlaceCallAsync(provisional);

        Assert.Equal(expected, _ringing.ToArray());
    }

    [Fact]
    public async Task Fires_again_for_the_next_call()
    {
        await PlaceCallAsync(SIPResponseStatusCodesEnum.Ringing);
        await PlaceCallAsync(SIPResponseStatusCodesEnum.SessionProgress);

        Assert.Equal(new[] { 180, 183 }, _ringing.ToArray());
    }

    [Fact]
    public async Task StatusMessage_still_reports_ringing()
    {
        await PlaceCallAsync(SIPResponseStatusCodesEnum.Ringing);

        Assert.Contains(_status, m => m.StartsWith("Call ringing: 180"));
    }
}
