using System.Diagnostics;
using System.Net;
using SIPSorcery.SIP;

namespace SipBotLib.Tests;

/// <summary>
/// A scripted SIP registrar on loopback UDP. Every REGISTER is recorded with a timestamp and
/// answered as <see cref="Script"/> says. With <see cref="ChallengeFirst"/>, an unauthenticated
/// REGISTER first gets a 401 digest challenge, so the script's answer lands on the
/// authenticated REGISTER.
/// </summary>
public sealed class FakeRegistrar : IDisposable
{
    public enum Reply { Ok, Drop, ServiceUnavailable, Forbidden, NotFound, PaymentRequired, Unauthorized }

    public sealed record Register(TimeSpan At, bool Authenticated, long Expires);

    private readonly SIPTransport _transport = new();
    private readonly object _lock = new();
    private readonly List<Register> _registers = new();
    private readonly HashSet<string> _branches = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public int Port { get; }
    public string Server => $"127.0.0.1:{Port}";
    public volatile bool ChallengeFirst;
    private Reply _script = Reply.Ok;

    public Reply Script
    {
        get { lock (_lock) return _script; }
        set { lock (_lock) _script = value; }
    }

    public FakeRegistrar()
    {
        var channel = new SIPUDPChannel(IPAddress.Loopback, 0);
        _transport.AddSIPChannel(channel);
        Port = channel.Port;
        _transport.SIPTransportRequestReceived += OnRequest;
    }

    public TimeSpan Now => _clock.Elapsed;

    public IReadOnlyList<Register> Registers
    {
        get { lock (_lock) return _registers.ToList(); }
    }

    public int Count
    {
        get { lock (_lock) return _registers.Count; }
    }

    /// <summary>Waits until at least <paramref name="count"/> REGISTERs have arrived.</summary>
    public async Task<bool> WaitForCountAsync(int count, TimeSpan timeout)
    {
        var deadline = _clock.Elapsed + timeout;
        while (_clock.Elapsed < deadline)
        {
            if (Count >= count)
                return true;
            await Task.Delay(20);
        }
        return Count >= count;
    }

    private Task OnRequest(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest req)
    {
        if (req.Method != SIPMethodsEnum.REGISTER)
            return Task.CompletedTask;

        bool authenticated = req.Header.AuthenticationHeaders.Count > 0;
        Reply reply;
        lock (_lock)
        {
            // Count transactions, not packets: a dropped REGISTER is retransmitted by the
            // client's transaction layer for ~32 s, even after the agent has been stopped.
            if (_branches.Add(req.Header.Vias.TopViaHeader?.Branch ?? Guid.NewGuid().ToString()))
                _registers.Add(new Register(_clock.Elapsed, authenticated, req.Header.Expires));
            reply = ChallengeFirst && !authenticated ? Reply.Unauthorized : _script;
        }

        SIPResponse response;
        switch (reply)
        {
            case Reply.Drop:
                return Task.CompletedTask;
            case Reply.Ok:
                response = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                response.Header.Contact = req.Header.Contact;
                response.Header.Expires = req.Header.Expires >= 0 ? req.Header.Expires : 60;
                break;
            case Reply.Unauthorized:
                response = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Unauthorised, null);
                response.Header.AuthenticationHeaders.Add(new SIPAuthenticationHeader(
                    SIPAuthorisationHeadersEnum.WWWAuthenticate, "lab", Guid.NewGuid().ToString("N")));
                break;
            default:
                response = SIPResponse.GetResponse(req, reply switch
                {
                    Reply.ServiceUnavailable => SIPResponseStatusCodesEnum.ServiceUnavailable,
                    Reply.Forbidden => SIPResponseStatusCodesEnum.Forbidden,
                    Reply.NotFound => SIPResponseStatusCodesEnum.NotFound,
                    Reply.PaymentRequired => SIPResponseStatusCodesEnum.PaymentRequired,
                    _ => throw new InvalidOperationException(reply.ToString())
                }, null);
                break;
        }
        return _transport.SendResponseAsync(response);
    }

    public void Dispose() => _transport.Shutdown();
}
