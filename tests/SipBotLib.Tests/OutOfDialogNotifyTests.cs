using System.Net;
using SIPSorcery.SIP;
using SipBot;
using Xunit;

namespace SipBotLib.Tests;

/// <summary>
/// An Asterisk-style PBX sends an unsolicited, out-of-dialog NOTIFY (Event: message-summary)
/// to the registered Contact. SipClient answers it with 200 OK at the transport level, like
/// OPTIONS, and leaves in-dialog NOTIFY (To tag present) to SIPUserAgent's dialog handling.
/// </summary>
public class OutOfDialogNotifyTests : IDisposable
{
    private readonly SIPTransport _clientTransport = new();
    private readonly SIPTransport _pbxTransport = new();
    private readonly SipClient _client;
    private readonly int _clientPort;

    public OutOfDialogNotifyTests()
    {
        var clientChannel = new SIPUDPChannel(IPAddress.Loopback, 0);
        _clientTransport.AddSIPChannel(clientChannel);
        _clientPort = clientChannel.Port;
        _pbxTransport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));

        // Loopback server: no STUN. Registration is never started, so nothing is sent.
        var config = new SipConfig { Server = "127.0.0.1", Username = "101", Password = "x" };
        _client = new SipClient(_clientTransport, config, enableAutoReconnection: false, enableHealthMonitoring: false);
    }

    public void Dispose()
    {
        _client.Dispose();
        _pbxTransport.Shutdown();
    }

    private SIPRequest MessageWaitingNotify(string? toTag)
    {
        var notify = SIPRequest.GetRequest(SIPMethodsEnum.NOTIFY, SIPURI.ParseSIPURI($"sip:101@127.0.0.1:{_clientPort}"));
        if (toTag != null)
            notify.Header.To.ToTag = toTag;
        notify.Header.Event = "message-summary";
        notify.Header.SubscriptionState = "terminated";
        notify.Header.ContentType = "application/simple-message-summary";
        notify.Body = "Messages-Waiting: no\r\nMessage-Account: sip:*97@127.0.0.1\r\n";
        return notify;
    }

    private async Task<SIPResponse?> SendAndWaitForResponse(SIPRequest request, TimeSpan wait)
    {
        var got = new TaskCompletionSource<SIPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pbxTransport.SIPTransportResponseReceived += (_, _, resp) =>
        {
            if (resp.Header.CallId == request.Header.CallId)
                got.TrySetResult(resp);
            return Task.CompletedTask;
        };
        await _pbxTransport.SendRequestAsync(request);
        var done = await Task.WhenAny(got.Task, Task.Delay(wait));
        return done == got.Task ? got.Task.Result : null;
    }

    [Fact]
    public async Task Out_of_dialog_message_summary_NOTIFY_is_answered_with_200()
    {
        var response = await SendAndWaitForResponse(MessageWaitingNotify(toTag: null), TimeSpan.FromSeconds(5));

        Assert.NotNull(response);
        Assert.Equal(SIPResponseStatusCodesEnum.Ok, response!.Status);
    }

    [Fact]
    public async Task In_dialog_NOTIFY_is_not_answered_by_the_transport_handler()
    {
        // With a To tag the NOTIFY belongs to a dialog; SipClient has none, so no 200 may come back.
        var response = await SendAndWaitForResponse(MessageWaitingNotify(toTag: "remote-tag"), TimeSpan.FromSeconds(1.5));

        Assert.True(response == null || response.Status != SIPResponseStatusCodesEnum.Ok,
            $"unexpected {response?.Status} for an in-dialog NOTIFY");
    }

    [Fact]
    public async Task OPTIONS_is_still_answered_with_200()
    {
        var options = SIPRequest.GetRequest(SIPMethodsEnum.OPTIONS, SIPURI.ParseSIPURI($"sip:101@127.0.0.1:{_clientPort}"));

        var response = await SendAndWaitForResponse(options, TimeSpan.FromSeconds(5));

        Assert.NotNull(response);
        Assert.Equal(SIPResponseStatusCodesEnum.Ok, response!.Status);
    }
}
