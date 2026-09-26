using Serilog;
using SIPSorcery.Media;
using System.Net;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SipBot;

/// <summary>Where <see cref="SipClient"/>'s registration stands.</summary>
public enum RegistrationState
{
    /// <summary><see cref="SipClient.StartRegistration"/> has not been called.</summary>
    NotStarted,
    /// <summary>A REGISTER is in progress and no result has arrived yet.</summary>
    Registering,
    Registered,
    /// <summary>The last attempt failed temporarily; a retry is scheduled or the agent's own retry is armed.</summary>
    TemporaryFailure,
    /// <summary>
    /// The registrar rejected the account (401/407 after authentication, 402, 403, 404). Nothing is
    /// retried until <see cref="SipClient.StartRegistration"/> is called again.
    /// </summary>
    HardFailure
}

/// <summary>
/// Enhanced SIP client with improved error handling, monitoring, and resource management.
/// </summary>
public class SipClient : IDisposable
{
    private const int DefaultRegistrationExpirySeconds = 60;
    private const int BlindTransferTimeoutSeconds = 10;

    /// <summary>
    /// How long <see cref="BlindTransferAsync"/> waits, after the REFER is accepted, for the final
    /// transfer-progress NOTIFY before it hangs up.
    /// </summary>
    internal static readonly TimeSpan TransferNotifyWait = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="Shutdown"/> waits for the registrar to confirm the unregister.</summary>
    internal static readonly TimeSpan UnregisterTimeout = TimeSpan.FromSeconds(3);

    private readonly string _sipUsername;
    private readonly string _sipPassword;
    private readonly string _sipServer;
    private readonly string _sipFromName;
    private readonly int _registrationExpirySeconds;
    private readonly bool _enableAutoReconnection;
    private readonly bool _enableHealthMonitoring;
    private readonly RegistrationRetryOptions _retry;

    private SIPTransport _sipTransport;
    private SIPUserAgent _userAgent;
    private SIPServerUserAgent? _pendingIncomingCall;
    private SIPRegistrationUserAgent _registrationAgent;
    private VoIPMediaSession? _mediaSession = null;

    // Thread safety and state management
    private readonly object _lockObject = new object();
    private volatile bool _isDisposed = false;
    private volatile bool _isShutdown = false;
    private volatile bool _isRegistered = false;
    private int _reconnectionAttempts = 0;
    private bool _reconnectPending;
    private bool _registrationStarted;
    private RegistrationState _registrationState = RegistrationState.NotStarted;
    private string? _lastRegistrationError;
    private Timer? _healthCheckTimer;
    private Timer? _reconnectionTimer;

    // Metrics and monitoring
    private readonly ConcurrentDictionary<string, long> _metrics = new();
    private readonly Stopwatch _callDurationTimer = new();
    private DateTime _lastRegistrationAttempt = DateTime.MinValue;
    private DateTime _lastSuccessfulRegistration = DateTime.MinValue;

    // Events
    public event Action<SipClient>? CallAnswer;
    public event Action<SipClient>? CallEnded;
    public event Action<SipClient, string>? StatusMessage;
    public event Action<SipClient>? RemotePutOnHold;
    public event Action<SipClient>? RemoteTookOffHold;
    public event Action<SipClient, Exception>? ErrorOccurred;
    public event Action<SipClient>? RegistrationStatusChanged;
    public event Action<SipClient, TimeSpan>? CallDurationUpdated;
    /// <summary>Raised when an INVITE arrives. Host should call <see cref="Accept"/> then <see cref="Answer"/>.</summary>
    public event Action<SipClient, SIPRequest>? IncomingCall;

    /// <summary>
    /// Raised when an RFC 4733 DTMF event completes on the remote RTP stream.
    /// Arguments are the raw telephone-event code and duration.
    /// </summary>
    public event Action<SipClient, byte, int>? DtmfReceived;

    // Transfer events
    public event Action<SipClient, string>? TransferInitiated;
    public event Action<SipClient>? TransferSucceeded;
    public event Action<SipClient, string>? TransferFailed;

    /// <summary>RFC2833 / telephone-event digit (0-9, *, #, A-D). Raised once per complete tone.</summary>
    public event Action<SipClient, char>? DtmfDigitReceived;

    // Properties
    /// <summary>
    /// True after a successful REGISTER. Cleared by any registration failure, temporary or hard,
    /// so it is false while registration is failing.
    /// </summary>
    public bool IsRegistered => _isRegistered;

    public RegistrationState RegistrationState => _registrationState;

    /// <summary>
    /// SIP status (e.g. <c>403 Forbidden</c>) or error text from the most recent registration
    /// failure. Kept after a later success so the last problem stays visible.
    /// </summary>
    public string? LastRegistrationError => _lastRegistrationError;
    public bool IsCallActive => _userAgent?.IsCallActive ?? false;

    /// <summary>
    /// SIP status or error from the last outbound <see cref="CallAsync"/> failure
    /// (e.g. <c>603 Decline</c>). Null after a successful dial or before any dial.
    /// </summary>
    public string? LastOutboundFailure { get; private set; }

    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim();
        // Strip a port: "127.0.0.1:5070", "localhost:5070", "[::1]:5070".
        if (h.StartsWith('[') && h.Contains(']'))
            h = h[1..h.IndexOf(']')];
        else if (h.Count(c => c == ':') == 1)
            h = h[..h.IndexOf(':')];
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (h is "127.0.0.1" or "::1") return true;
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }
    public TimeSpan CurrentCallDuration => _callDurationTimer.IsRunning ? _callDurationTimer.Elapsed : TimeSpan.Zero;
    public DateTime LastSuccessfulRegistration => _lastSuccessfulRegistration;
    public IReadOnlyDictionary<string, long> Metrics => _metrics;
    public string Username => _sipUsername;
    public string Server => _sipServer;

    public SipClient(
        SIPTransport sipTransport,
        SipConfig sipSettings,
        int registrationExpirySeconds = DefaultRegistrationExpirySeconds,
        bool enableAutoReconnection = true,
        bool enableHealthMonitoring = true)
    {
        _sipTransport = sipTransport ?? throw new ArgumentNullException(nameof(sipTransport));
        _sipUsername = sipSettings?.Username ?? throw new ArgumentNullException(nameof(sipSettings));
        _sipPassword = sipSettings.Password;
        _sipServer = sipSettings.Server;
        _sipFromName = sipSettings.FromName;
        _registrationExpirySeconds = Math.Max(30, registrationExpirySeconds); // Minimum 30 seconds
        _enableAutoReconnection = enableAutoReconnection;
        _enableHealthMonitoring = enableHealthMonitoring;
        _retry = sipSettings.RegistrationRetry ?? new RegistrationRetryOptions();

        InitializeTransport();
        InitializeRegistrationAgent();
        InitializeUserAgent();

        if (_enableHealthMonitoring)
        {
            StartHealthMonitoring();
        }

        Log.Information($"SIP Client initialized for {_sipUsername}@{_sipServer}");
    }

    private void InitializeTransport()
    {
        try
        {
            _sipTransport.SIPRequestInTraceEvent += (localSIPEndPoint, endPoint, request) =>
                Log.Debug($"SIP Request Received: {request.Method} from {endPoint}");

            _sipTransport.SIPResponseOutTraceEvent += (localSIPEndPoint, endPoint, response) =>
                Log.Debug($"SIP Response Sent: {response.Status} to {endPoint}");

            // Contact host for REGISTER:
            // - When PBX is loopback (same host as Asterisk), use 127.0.0.1 — STUN public IP
            //   is wrong and IPv6 STUN results break SIP URI parsing without brackets.
            // - Otherwise STUN public IPv4 for NAT traversal.
            bool localPbx = IsLoopbackHost(_sipServer);
            string? contactHost = null;
            if (localPbx)
            {
                contactHost = "127.0.0.1";
                Log.Information("SIP ContactHost=127.0.0.1 (PBX server is loopback; skipping STUN)");
            }
            else
            {
                StunHelper.SetupStun();
                var stunIp = StunHelper.PublicIPAddress;
                // Prefer IPv4 Contact; unbracketed IPv6 breaks SIPURI.ParseSIPURI.
                if (!string.IsNullOrWhiteSpace(stunIp)
                    && IPAddress.TryParse(stunIp, out var parsed)
                    && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    contactHost = stunIp;
                    Log.Information("SIP ContactHost set to STUN public IPv4 {Ip} for NAT traversal", stunIp);
                }
                else if (!string.IsNullOrWhiteSpace(stunIp))
                {
                    Log.Warning("STUN returned non-IPv4 {Ip}; leaving ContactHost unset", stunIp);
                }
                else
                {
                    Log.Warning(
                        "STUN public IP unresolved; REGISTER Contact will use the local bind address " +
                        "(inbound calls may fail behind NAT)");
                }
            }

            if (!string.IsNullOrWhiteSpace(contactHost))
                _sipTransport.ContactHost = contactHost;

            // SIPSorcery's default REGISTER Contact is "sip:host:port" with no user part. VitalPBX
            // is happier with "sip:ext@host:port" (matches the AOR). Customise after ContactHost
            // so we own the final URI; return the header to replace.
            _sipTransport.CustomiseRequestHeader = (localEP, remoteEP, req) =>
            {
                if (req.Method != SIPMethodsEnum.REGISTER)
                    return null!;

                string host = contactHost
                    ?? (localEP.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        ? localEP.Address.ToString()
                        : "127.0.0.1");
                int port = localEP.Port > 0 ? localEP.Port : req.Header.Contact?.FirstOrDefault()?.ContactURI?.ToSIPEndPoint()?.Port ?? 5060;
                // Prefer the listen port from the contact the stack already built, if present.
                var existing = req.Header.Contact?.FirstOrDefault()?.ContactURI;
                if (existing != null && existing.ToSIPEndPoint() != null)
                    port = existing.ToSIPEndPoint()!.Port;

                var contactUri = new SIPURI(
                    _sipUsername,
                    $"{host}:{port}",
                    null,
                    SIPSchemesEnum.sip,
                    SIPProtocolsEnum.udp);
                req.Header.Contact = new List<SIPContactHeader> { new SIPContactHeader(null, contactUri) };
                return req.Header;
            };

            // Asterisk/VitalPBX "qualify" probes the Contact with OPTIONS. SIPUserAgent does not
            // auto-answer OPTIONS; without a 200 the peer is marked unreachable and callers get
            // busy even though REGISTER succeeded. Confirmed live: OPTIONS to this process on the
            // LAN timed out with zero response before this handler existed.
            // The same handler answers out-of-dialog NOTIFY (see OnTransportRequestReceived).
            _sipTransport.SIPTransportRequestReceived += OnTransportRequestReceived;

            IncrementMetric("transport_initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SIP transport");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    private Task OnTransportRequestReceived(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest req)
    {
        // Asterisk sends an unsolicited NOTIFY (Event: message-summary) to the registered
        // Contact for an extension with a mailbox. It is outside any dialog (no To tag), so
        // SIPUserAgent ignores it; unanswered, the PBX retransmits it for about 32 s after every
        // registration. In-dialog NOTIFY (e.g. REFER progress) carries a To tag and is answered
        // by SIPUserAgent's dialog handling, so it is left alone here.
        bool outOfDialogNotify = req.Method == SIPMethodsEnum.NOTIFY
            && string.IsNullOrEmpty(req.Header.To?.ToTag);

        if (req.Method != SIPMethodsEnum.OPTIONS && !outOfDialogNotify)
            return Task.CompletedTask;

        try
        {
            var okResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
            Log.Debug($"Answering {req.Method} from {remoteEP} with 200 OK");
            return _sipTransport.SendResponseAsync(okResponse);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to answer {req.Method} from {remoteEP}");
            return Task.CompletedTask;
        }
    }

    private void InitializeRegistrationAgent()
    {
        try
        {
            _registrationAgent = CreateRegistrationAgent();
            IncrementMetric("registration_agent_initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize registration agent");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    private void InitializeUserAgent()
    {
        try
        {
            _userAgent = CreateNewUserAgent(_sipTransport);
            IncrementMetric("user_agent_initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize user agent");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    private SIPUserAgent CreateNewUserAgent(SIPTransport sipTransport)
    {
        var userAgent = new SIPUserAgent(sipTransport, null);
        userAgent.ClientCallTrying += CallTrying;
        userAgent.ClientCallRinging += CallRinging;
        userAgent.ClientCallAnswered += CallAnswered;
        userAgent.ClientCallFailed += CallFailed;
        userAgent.OnCallHungup += CallFinished;
        userAgent.ServerCallCancelled += IncomingCallCancelled;
        userAgent.OnIncomingCall += OnIncomingCall;
        userAgent.OnDtmfTone += OnDtmfTone;
        return userAgent;
    }

    private SIPRegistrationUserAgent CreateRegistrationAgent()
    {
        var agent = new SIPRegistrationUserAgent(
            _sipTransport,
            _sipUsername,
            _sipPassword,
            _sipServer,
            _registrationExpirySeconds,
            maxRegistrationAttemptTimeout: _retry.AgentAttemptTimeoutSeconds,
            registerFailureRetryInterval: _retry.AgentFailureRetrySeconds);

        agent.RegistrationSuccessful += OnRegistrationSuccessful;
        agent.RegistrationFailed += OnRegistrationFailed;
        agent.RegistrationTemporaryFailure += OnRegistrationTemporaryFailure;
        agent.RegistrationRemoved += OnRegistrationRemoved;
        return agent;
    }

    private void DetachRegistrationAgent(SIPRegistrationUserAgent agent)
    {
        agent.RegistrationSuccessful -= OnRegistrationSuccessful;
        agent.RegistrationFailed -= OnRegistrationFailed;
        agent.RegistrationTemporaryFailure -= OnRegistrationTemporaryFailure;
        agent.RegistrationRemoved -= OnRegistrationRemoved;
    }

    /// <summary>
    /// Stops the current registration agent without an unregister and detaches it, so it can
    /// neither send another REGISTER (including SIPSorcery's repeating timer after a hard failure)
    /// nor report late results. Caller holds <see cref="_lockObject"/>.
    /// </summary>
    private void StopRegistrationAgentLocked()
    {
        DetachRegistrationAgent(_registrationAgent);
        _registrationAgent.Stop(sendZeroExpiryRegister: false);
    }

    /// <summary>
    /// Stops the current agent (no unregister) and starts a fresh one. A fresh agent never throws
    /// "already running", and its results cannot be confused with a late event from the old one.
    /// Caller holds <see cref="_lockObject"/>.
    /// </summary>
    private void RestartRegistrationAgentLocked()
    {
        StopRegistrationAgentLocked();
        _registrationAgent = CreateRegistrationAgent();
        _lastRegistrationAttempt = DateTime.UtcNow;
        _registrationState = RegistrationState.Registering;
        _registrationAgent.Start();
    }

    /// <summary>
    /// Starts registration, or restarts it if it is already running. Safe to call again at any
    /// time, including after a hard failure (see <see cref="RegistrationState.HardFailure"/>): the
    /// current agent is stopped without an unregister, then a new one starts.
    /// </summary>
    public void StartRegistration()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        lock (_lockObject)
        {
            try
            {
                StopReconnectionTimer();
                _registrationStarted = true;
                _reconnectionAttempts = 0;
                RestartRegistrationAgentLocked();
                StatusMessage?.Invoke(this, $"Registration attempt for {_sipUsername}@{_sipServer} started.");
                IncrementMetric("registration_attempts");
                Log.Information($"Starting SIP registration for {_sipUsername}@{_sipServer}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start registration");
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }
    }

    public void Accept(SIPRequest sipRequest)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        try
        {
            var acceptedCall = _userAgent.AcceptCall(sipRequest);
            lock (_lockObject)
            {
                _pendingIncomingCall = acceptedCall;
            }
            IncrementMetric("incoming_calls_accepted");
            Log.Information($"Accepted incoming call from {sipRequest.Header.From}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to accept incoming call");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public async Task<bool> Answer(IAudioSink audioSink, IAudioSource audioSource)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        SIPServerUserAgent? pendingCall;
        lock (_lockObject)
        {
            pendingCall = _pendingIncomingCall;
        }

        if (pendingCall == null)
        {
            StatusMessage?.Invoke(this, "There was no pending call available to answer.");
            return false;
        }

        try
        {
            var sipRequest = pendingCall.ClientTransaction.TransactionRequest;

            bool hasAudio = true;
            bool hasVideo = false;

            if (sipRequest.Body != null)
            {
                SDP offerSDP = SDP.ParseSDPDescription(sipRequest.Body);
                hasAudio = offerSDP.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.MediaStreamStatus != MediaStreamStatusEnum.Inactive);
                hasVideo = offerSDP.Media.Any(x => x.Media == SDPMediaTypesEnum.video && x.MediaStreamStatus != MediaStreamStatusEnum.Inactive);
            }

            var mediaSession = CreateMediaSession(CreateMediaEndPoints(audioSink, audioSource));
            // DTMF (RFC2833 telephone-event) for keypad PIN collection
            mediaSession.OnRtpEvent += HandleRtpEvent;
            lock (_lockObject)
            {
                _mediaSession = mediaSession;
            }

            bool result = await _userAgent.Answer(pendingCall, mediaSession);
            lock (_lockObject)
            {
                _pendingIncomingCall = null;
            }

            if (result)
            {
                _callDurationTimer.Restart();
                CallAnswer?.Invoke(this);
                IncrementMetric("calls_answered");
                Log.Information("Call successfully answered");
            }
            else
            {
                IncrementMetric("call_answer_failures");
                Log.Warning("Failed to answer call");
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred while answering call");
            ErrorOccurred?.Invoke(this, ex);
            IncrementMetric("call_answer_exceptions");
            return false;
        }
    }

    /// <summary>
    /// Places an outbound call. Destination may be a full SIP URI, <c>tel:</c> URI,
    /// <c>user@host</c>, a bare extension, or a PSTN number (E.164 <c>+</c> is stripped
    /// to digits). Media is attached the same way as <see cref="Answer"/>. Existing events
    /// (<see cref="CallAnswer"/>, <see cref="CallEnded"/>, DTMF, status) are raised on the
    /// same path as inbound calls. On failure, <see cref="LastOutboundFailure"/> holds the
    /// SIP status when the far end sent one.
    /// </summary>
    public async Task<bool> CallAsync(
        string destination,
        IAudioSink audioSink,
        IAudioSource audioSource,
        int ringTimeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(SipClient));

        if (IsCallActive)
        {
            var msg = "Cannot dial: a call is already active.";
            StatusMessage?.Invoke(this, msg);
            Log.Warning(msg);
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(audioSink);
        ArgumentNullException.ThrowIfNull(audioSource);

        LastOutboundFailure = null;
        string uri;
        try
        {
            uri = SipUriNormalizer.Normalize(destination, _sipServer);
        }
        catch (ArgumentException ex)
        {
            LastOutboundFailure = ex.Message;
            Log.Warning(ex, "Rejected outbound destination");
            return false;
        }

        try
        {
            var mediaSession = CreateMediaSession(CreateMediaEndPoints(audioSink, audioSource));
            lock (_lockObject)
            {
                _mediaSession = mediaSession;
            }

            using var cancelReg = cancellationToken.Register(() =>
            {
                try { _userAgent.Cancel(); }
                catch (Exception ex) { Log.Debug(ex, "Cancel during outbound dial"); }
            });

            StatusMessage?.Invoke(this, $"Dialing {uri}...");
            Log.Information("Outbound call to {Uri}", uri);
            IncrementMetric("outbound_call_attempts");

            bool result = await _userAgent.Call(uri, _sipUsername, _sipPassword, mediaSession, ringTimeoutSeconds)
                .ConfigureAwait(false);

            if (result)
            {
                if (!_callDurationTimer.IsRunning)
                    _callDurationTimer.Restart();
                IncrementMetric("outbound_calls_answered");
                Log.Information("Outbound call answered: {Uri}", uri);
            }
            else
            {
                IncrementMetric("outbound_call_failures");
                LastOutboundFailure ??= "not answered";
                Log.Warning("Outbound call failed or was not answered: {Uri} ({Reason})", uri, LastOutboundFailure);
            }

            return result;
        }
        catch (Exception ex)
        {
            LastOutboundFailure ??= ex.Message;
            Log.Error(ex, "Exception placing outbound call to {Uri}", uri);
            ErrorOccurred?.Invoke(this, ex);
            IncrementMetric("outbound_call_exceptions");
            return false;
        }
    }

    public void Hangup()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            if (_userAgent.IsCallActive)
            {
                // SIPUserAgent.Hangup() already raises OnCallHungup (wired to CallFinished
                // in CreateNewUserAgent), which itself raises CallEnded. Don't invoke either
                // again here or CallEnded/metrics get double-counted for every hangup.
                _userAgent.Hangup();
                IncrementMetric("calls_hungup");
                Log.Information("Call hung up");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred while hanging up");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Performs a blind transfer to a full SIP URI (e.g., "sip:100@pbx.example.com").
    /// The original call leg is hung up on success.
    /// </summary>
    /// <param name="sipUri">The target SIP URI.</param>
    /// <param name="timeout">Optional timeout for the transfer (default 10s).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>True if transfer succeeded, false otherwise.</returns>
    public async Task<bool> BlindTransferAsync(
        string sipUri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        if (!IsCallActive)
        {
            var msg = "Cannot transfer: No active call.";
            StatusMessage?.Invoke(this, msg);
            TransferFailed?.Invoke(this, msg);
            Log.Warning(msg);
            return false;
        }

        // The transferee (Asterisk here) reports progress with in-dialog NOTIFYs carrying a sipfrag
        // ("SIP/2.0 100 Trying" ... "SIP/2.0 200 OK"). SIPUserAgent answers them while the dialog
        // exists; hanging up before they arrive leaves them unanswered and the PBX retransmits them.
        // Subscribe before sending the REFER: they can arrive right behind the 202.
        var finalNotify = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTransferNotify(string sipfrag)
        {
            string statusLine = sipfrag.Split('\n')[0].Trim();
            if (statusLine.StartsWith("SIP/2.0 ") && statusLine.Length >= 11
                && int.TryParse(statusLine.AsSpan(8, 3), out int code) && code >= 200)
            {
                finalNotify.TrySetResult(statusLine);
            }
        }
        _userAgent.OnTransferNotify += OnTransferNotify;

        try
        {
            sipUri = SipUriNormalizer.Normalize(sipUri, _sipServer);
            if (!SIPURI.TryParse(sipUri, out var destination))
            {
                var msg = $"Invalid SIP URI: {sipUri}";
                StatusMessage?.Invoke(this, msg);
                TransferFailed?.Invoke(this, msg);
                Log.Warning(msg);
                return false;
            }

            var transferTimeout = timeout ?? TimeSpan.FromSeconds(BlindTransferTimeoutSeconds);
            TransferInitiated?.Invoke(this, sipUri);
            StatusMessage?.Invoke(this, $"Initiating blind transfer to {sipUri}...");
            Log.Information($"Blind transfer initiated to {sipUri}");

            var result = await _userAgent.BlindTransfer(destination, transferTimeout, cancellationToken);

            if (result)
            {
                TransferSucceeded?.Invoke(this);
                StatusMessage?.Invoke(this, $"Blind transfer to {sipUri} succeeded.");
                Log.Information($"Blind transfer to {sipUri} succeeded");

                // Let the final progress NOTIFY arrive (and be answered in the dialog) first. Not
                // every transferee sends one, so the wait is bounded.
                var first = await Task.WhenAny(finalNotify.Task, Task.Delay(TransferNotifyWait)).ConfigureAwait(false);
                Log.Information("Transfer progress: {Outcome}", first == finalNotify.Task
                    ? finalNotify.Task.Result
                    : $"no final NOTIFY within {TransferNotifyWait.TotalSeconds:0} s");

                // The REFER was accepted, so the transferee now belongs to the transfer target.
                // PBXs such as Asterisk take this leg out of the bridge but leave it up; end it
                // here (BYE), which raises CallEnded as for any hangup.
                Hangup();
            }
            else
            {
                var msg = $"Blind transfer to {sipUri} failed (timeout or rejection).";
                TransferFailed?.Invoke(this, msg);
                StatusMessage?.Invoke(this, msg);
                Log.Warning(msg);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            var msg = "Blind transfer cancelled.";
            TransferFailed?.Invoke(this, msg);
            Log.Warning(msg);
            return false;
        }
        catch (Exception ex)
        {
            var msg = $"Exception during blind transfer to {sipUri}: {ex.Message}";
            StatusMessage?.Invoke(this, msg);
            TransferFailed?.Invoke(this, msg);
            ErrorOccurred?.Invoke(this, ex);
            Log.Error(ex, msg);
            return false;
        }
        finally
        {
            _userAgent.OnTransferNotify -= OnTransferNotify;
        }
    }

    /// <summary>
    /// Performs a blind transfer to an internal extension (e.g., "100").
    /// Constructs URI as sip:{extension}@{_sipServer}.
    /// </summary>
    /// <param name="extension">The target extension.</param>
    /// <param name="timeout">Optional timeout for the transfer (default 10s).</param>
    public Task<bool> BlindTransferToExtensionAsync(string extension, TimeSpan? timeout = null) =>
        BlindTransferAsync($"sip:{extension}@{_sipServer}", timeout ?? TimeSpan.FromSeconds(BlindTransferTimeoutSeconds));

    public void Shutdown()
    {
        if (_isShutdown)
        {
            return;
        }
        _isShutdown = true;

        try
        {
            Log.Information("Shutting down SIP client");

            Hangup();

            VoIPMediaSession? mediaSessionToDispose;
            lock (_lockObject)
            {
                mediaSessionToDispose = _mediaSession;
                _mediaSession = null;
            }
            mediaSessionToDispose?.Close("Shutdown");
            mediaSessionToDispose?.Dispose();

            StopHealthMonitoring();
            lock (_lockObject)
            {
                StopReconnectionTimer();
            }

            // Unregister before the transport goes away; otherwise the registrar keeps a dead
            // contact until it expires and forks calls to it.
            UnregisterBeforeShutdown();

            _sipTransport.Shutdown();
            
            IncrementMetric("shutdowns");
            Log.Information("SIP client shutdown completed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred during shutdown");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Sends the unregister (REGISTER with Expires 0) and waits up to
    /// <see cref="UnregisterTimeout"/> for the registrar to confirm it. The unregister is usually
    /// challenged (401, then an authenticated retry), so shutting the transport down right after
    /// <see cref="SIPRegistrationUserAgent.Stop"/> used to lose it. When not registered, the agent
    /// is just stopped and nothing is sent.
    /// </summary>
    private void UnregisterBeforeShutdown()
    {
        SIPRegistrationUserAgent agent;
        bool registered;
        lock (_lockObject)
        {
            agent = _registrationAgent;
            registered = _isRegistered;
        }

        if (!registered)
        {
            agent.Stop(sendZeroExpiryRegister: false);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        void Removed(SIPURI uri, SIPResponse resp) => done.Set();
        void Failed(SIPURI uri, SIPResponse resp, string error) => done.Set();
        agent.RegistrationRemoved += Removed;
        agent.RegistrationFailed += Failed;
        agent.RegistrationTemporaryFailure += Failed;
        try
        {
            agent.Stop(sendZeroExpiryRegister: true);
            if (done.Wait(UnregisterTimeout))
                Log.Information("Unregistered {User}@{Server}", _sipUsername, _sipServer);
            else
                Log.Warning("Unregister of {User}@{Server} not confirmed within {Timeout}; the registrar keeps the contact until it expires",
                    _sipUsername, _sipServer, UnregisterTimeout);
        }
        finally
        {
            agent.RegistrationRemoved -= Removed;
            agent.RegistrationFailed -= Failed;
            agent.RegistrationTemporaryFailure -= Failed;
        }
    }

    private MediaEndPoints CreateMediaEndPoints(IAudioSink audioSink, IAudioSource audioSource)
    {
        var mediaEndPoints = new MediaEndPoints
        {
            AudioSink = audioSink,
            AudioSource = audioSource,
            VideoSink = null,
            VideoSource = null
        };
        return mediaEndPoints;
    }

    private VoIPMediaSession CreateMediaSession(MediaEndPoints mediaEndPoints)
    {
        var voipMediaSession = new VoIPMediaSession(mediaEndPoints);
        // Default (false) drops inbound RTP whose source doesn't exactly match the negotiated SDP
        // address -- very common against real PBX/trunk providers that relay media from a different
        // address/port than they advertised (observed live: call answers fine, zero RTP frames ever
        // arrive, then SIPSorcery's own 30s RTP-timeout hangs up the call). SIPSorcery's own call
        // examples set this to true for exactly this reason.
        voipMediaSession.AcceptRtpFromAny = true;
        Log.Information($"[{GetType().Name}] Created with AudioSink={mediaEndPoints.AudioSink.GetType().Name}, AcceptRtpFromAny={voipMediaSession.AcceptRtpFromAny}");
        return voipMediaSession;
    }

    // RFC2833 DTMF: emit once per tone when EndOfEvent is true.
    private void HandleRtpEvent(IPEndPoint remoteEP, RTPEvent rtpEvent, RTPHeader header)
    {
        try
        {
            if (!rtpEvent.EndOfEvent)
                return;

            // EventID 0-9 = digits, 10=*, 11=#, 12-15=A-D
            int id = rtpEvent.EventID;
            char? ch = id switch
            {
                >= 0 and <= 9 => (char)('0' + id),
                10 => '*',
                11 => '#',
                12 => 'A',
                13 => 'B',
                14 => 'C',
                15 => 'D',
                _ => null
            };
            if (ch is char digit)
            {
                Log.Information("DTMF digit received: {Digit}", digit);
                DtmfDigitReceived?.Invoke(this, digit);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error handling RTP DTMF event");
        }
    }

    // Event handlers
    private void OnRegistrationSuccessful(SIPURI uri, SIPResponse resp)
    {
        lock (_lockObject)
        {
            _isRegistered = true;
            _reconnectionAttempts = 0;
            _lastSuccessfulRegistration = DateTime.UtcNow;
            _registrationState = RegistrationState.Registered;
        }

        StatusMessage?.Invoke(this, $"Registration successful for {uri}. Expires: {resp.Header.Expires}");
        RegistrationStatusChanged?.Invoke(this);
        IncrementMetric("successful_registrations");
        Log.Debug($"SIP registration successful for {uri}");
    }

    // SIPSorcery reports some of these as RegistrationFailed and others (e.g. a 402 on the
    // first REGISTER) as RegistrationTemporaryFailure, so classify by status, not by event.
    // A 401/407 only surfaces after SIPSorcery has already answered the challenge, i.e. the
    // credentials were rejected.
    private static bool IsHardRegistrationFailure(SIPResponse? resp) => resp?.Status is
        SIPResponseStatusCodesEnum.Unauthorised or
        SIPResponseStatusCodesEnum.ProxyAuthenticationRequired or
        SIPResponseStatusCodesEnum.PaymentRequired or
        SIPResponseStatusCodesEnum.Forbidden or
        SIPResponseStatusCodesEnum.NotFound;

    private void OnRegistrationFailed(SIPURI uri, SIPResponse resp, string error) =>
        HandleRegistrationFailure(uri, resp, error);

    private void OnRegistrationTemporaryFailure(SIPURI uri, SIPResponse resp, string error) =>
        HandleRegistrationFailure(uri, resp, error);

    private void HandleRegistrationFailure(SIPURI uri, SIPResponse? resp, string error)
    {
        bool hard = IsHardRegistrationFailure(resp);
        lock (_lockObject)
        {
            _isRegistered = false;
            _lastRegistrationError = resp != null ? $"{(int)resp.Status} {resp.ReasonPhrase}".Trim() : error;
            if (hard)
            {
                // SIPSorcery stops re-arming after a hard failure, but a failure on the first
                // REGISTER after Start() leaves Start()'s repeating timer running, which would
                // send the rejected credentials again every (Expires - 5) s. Stop the agent so no
                // further REGISTER goes out until StartRegistration() is called.
                _registrationState = RegistrationState.HardFailure;
                StopReconnectionTimer();
                StopRegistrationAgentLocked();
            }
            else
            {
                _registrationState = RegistrationState.TemporaryFailure;
            }
        }

        StatusMessage?.Invoke(this, hard
            ? $"Registration rejected for {uri}: {error}. Not retrying until StartRegistration() is called."
            : $"Registration temporary failure for {uri}: {error}");
        RegistrationStatusChanged?.Invoke(this);
        IncrementMetric(hard ? "failed_registrations" : "temporary_registration_failures");
        if (hard)
            Log.Error("SIP registration rejected for {Uri}: {Error}. Not retrying until StartRegistration() is called", uri, error);
        else
            Log.Warning("SIP registration temporary failure for {Uri}: {Error}", uri, error);

        if (!hard && _enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnRegistrationRemoved(SIPURI uri, SIPResponse resp)
    {
        lock (_lockObject)
        {
            _isRegistered = false;
            _registrationState = RegistrationState.TemporaryFailure;
        }

        StatusMessage?.Invoke(this, $"Registration removed for {uri}");
        RegistrationStatusChanged?.Invoke(this);
        IncrementMetric("removed_registrations");
        Log.Warning($"SIP registration removed for {uri}");

        if (_enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnIncomingCall(SIPUserAgent userAgent, SIPRequest sipRequest)
    {
        IncrementMetric("incoming_calls");
        Log.Information($"Incoming call from {sipRequest.Header.From}");
        IncomingCall?.Invoke(this, sipRequest);
    }

    private void CallTrying(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call trying: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        IncrementMetric("call_trying");
    }

    private void CallRinging(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call ringing: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        IncrementMetric("call_ringing");
    }

    private void CallFailed(ISIPClientUserAgent uac, string errorMessage, SIPResponse? failureResponse)
    {
        LastOutboundFailure = failureResponse != null
            ? $"{failureResponse.StatusCode} {failureResponse.ReasonPhrase}".Trim()
            : errorMessage;
        StatusMessage?.Invoke(this, "Call failed: " + errorMessage + ".");
        IncrementMetric("call_failures");
        CallFinished(null);
        Log.Warning("Call failed: {Error} ({Status})", errorMessage, LastOutboundFailure);
    }

    private void CallAnswered(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call answered: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        if (!_callDurationTimer.IsRunning)
            _callDurationTimer.Restart();
        CallAnswer?.Invoke(this);
        IncrementMetric("calls_answered");
    }

    private void OnDtmfTone(byte tone, int duration)
    {
        IncrementMetric("dtmf_received");
        Log.Information("DTMF tone {Tone} duration {Duration}", tone, duration);
        DtmfReceived?.Invoke(this, tone, duration);
    }

    private void CallFinished(SIPDialogue? dialogue)
    {
        if (_callDurationTimer.IsRunning)
        {
            _callDurationTimer.Stop();
            var duration = _callDurationTimer.Elapsed;
            CallDurationUpdated?.Invoke(this, duration);
            Log.Information($"Call ended. Duration: {duration:mm\\:ss}");
        }

        VoIPMediaSession? mediaSessionToDispose;
        lock (_lockObject)
        {
            mediaSessionToDispose = _mediaSession;
            _mediaSession = null;
            _pendingIncomingCall = null;
        }
        mediaSessionToDispose?.Close("Call Finished");
        mediaSessionToDispose?.Dispose();
        CallEnded?.Invoke(this);
        IncrementMetric("calls_ended");
    }

    private void IncomingCallCancelled(ISIPServerUserAgent uas, SIPRequest cancelRequest)
    {
        IncrementMetric("incoming_calls_cancelled");
        CallFinished(null);
    }

    // Health monitoring and reconnection
    private void StartHealthMonitoring()
    {
        _healthCheckTimer = new Timer(PerformHealthCheck, null, _retry.HealthCheckIntervalMs, _retry.HealthCheckIntervalMs);
        Log.Debug("Health monitoring started");
    }

    private void StopHealthMonitoring()
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;
    }

    private void PerformHealthCheck(object? state)
    {
        try
        {
            var timeSinceLastRegistration = DateTime.UtcNow - _lastSuccessfulRegistration;

            // Only after the host has started registration, never after a hard failure (the
            // registrar rejected the account; retrying would just repeat the rejection), never
            // while an attempt is in flight (it reports its own result; interrupting it would stop
            // that agent and skip a backoff step), and never during shutdown.
            if (!_registrationStarted || _isShutdown
                || _registrationState is RegistrationState.HardFailure or RegistrationState.Registering)
                return;

            // If we haven't registered successfully recently, try to re-register. Route through
            // ScheduleReconnection() so this respects the same attempt cap and backoff as
            // failure-driven reconnects, instead of hammering the server every check.
            if (timeSinceLastRegistration.TotalMilliseconds > _retry.HealthCheckStaleMs && !_isRegistered)
            {
                if (_enableAutoReconnection)
                {
                    Log.Warning("Health check: no successful registration recently, scheduling re-registration");
                    ScheduleReconnection();
                }
                else
                {
                    Log.Warning("Health check: no successful registration recently, restarting registration");
                    StartRegistration();
                }
            }

            IncrementMetric("health_checks");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception during health check");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Schedules one reconnect attempt after a temporary failure. At most one attempt is pending
    /// at a time, so the health check and failure events cannot stack REGISTERs.
    /// </summary>
    /// <remarks>
    /// Default retry: attempt n after <see cref="RegistrationRetryOptions.DefaultBaseDelayMs"/> × n,
    /// up to <see cref="RegistrationRetryOptions.DefaultMaxAttempts"/>. After the last, the
    /// registration agent is left running, so its own retry still recovers the process.
    /// Extended retry: the agent is stopped (the client owns the retries) and attempts continue
    /// forever, the delay doubling from the initial delay up to the cap.
    /// </remarks>
    internal void ScheduleReconnection()
    {
        lock (_lockObject)
        {
            // Checked under the lock: the health check reads the state without it, so by the time
            // it gets here an attempt it did not know about may already be in flight. Scheduling
            // then would start an attempt no failure asked for and, in extended mode, stop the
            // in-flight agent before it can report its result.
            if (_isShutdown || _reconnectPending
                || _registrationState is RegistrationState.HardFailure or RegistrationState.Registering)
                return;

            long delay;
            if (_retry.Extended)
            {
                _reconnectionAttempts++;
                double backoff = _retry.ExtendedInitialDelayMs * Math.Pow(2, Math.Min(_reconnectionAttempts - 1, 30));
                delay = (long)Math.Min(backoff, _retry.ExtendedMaxDelayMs);
                StopRegistrationAgentLocked();
            }
            else
            {
                if (_reconnectionAttempts >= _retry.DefaultMaxAttempts)
                {
                    if (_reconnectionAttempts == _retry.DefaultMaxAttempts)
                    {
                        _reconnectionAttempts++;
                        Log.Warning(
                            "Reconnection attempts ({Max}) used; the registration agent's own retry is still armed",
                            _retry.DefaultMaxAttempts);
                    }
                    return;
                }

                _reconnectionAttempts++;
                delay = (long)_retry.DefaultBaseDelayMs * _reconnectionAttempts;
            }

            _reconnectionTimer?.Dispose();
            _reconnectionTimer = new Timer(AttemptReconnection, null, delay, Timeout.Infinite);
            _reconnectPending = true;

            Log.Information($"Scheduling reconnection attempt {_reconnectionAttempts} in {delay}ms");
        }
    }

    private void StopReconnectionTimer()
    {
        _reconnectionTimer?.Dispose();
        _reconnectionTimer = null;
        _reconnectPending = false;
    }

    private void AttemptReconnection(object? state)
    {
        try
        {
            lock (_lockObject)
            {
                _reconnectPending = false;
                if (_isShutdown || _isDisposed || _registrationState == RegistrationState.HardFailure)
                    return;

                Log.Information($"Attempting reconnection (attempt {_reconnectionAttempts})");
                RestartRegistrationAgentLocked();
            }
            IncrementMetric("reconnection_attempts");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception during reconnection attempt");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    // Metrics
    private void IncrementMetric(string metricName)
    {
        _metrics.AddOrUpdate(metricName, 1, (key, value) => value + 1);
    }

    // IDisposable implementation
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        // Run cleanup (which hangs up any active call, disposes the media session,
        // stops the registration agent/timers, and shuts down the transport) BEFORE
        // flipping _isDisposed, since Shutdown()/Hangup() early-return once disposed.
        Shutdown();
        _isDisposed = true;

        Log.Information("SIP Client disposed");
    }
}
