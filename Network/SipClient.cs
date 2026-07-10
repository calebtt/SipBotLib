using Serilog;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SipBot;

/// <summary>
/// Enhanced SIP client with improved error handling, monitoring, and resource management.
/// </summary>
public class SipClient : IDisposable
{
    private const int DefaultRegistrationExpirySeconds = 60;
    private const int MaxReconnectionAttempts = 5;
    private const int ReconnectionDelayMs = 2000;
    private const int HealthCheckIntervalMs = 30000; // 30 seconds
    private const int BlindTransferTimeoutSeconds = 10;

    private readonly string _sipUsername;
    private readonly string _sipPassword;
    private readonly string _sipServer;
    private readonly string _sipFromName;
    private readonly int _registrationExpirySeconds;
    private readonly bool _enableAutoReconnection;
    private readonly bool _enableHealthMonitoring;

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

    // Transfer events
    public event Action<SipClient, string>? TransferInitiated;
    public event Action<SipClient>? TransferSucceeded;
    public event Action<SipClient, string>? TransferFailed;

    // Properties
    public bool IsRegistered => _isRegistered;
    public bool IsCallActive => _userAgent?.IsCallActive ?? false;
    public TimeSpan CurrentCallDuration => _callDurationTimer.IsRunning ? _callDurationTimer.Elapsed : TimeSpan.Zero;
    public DateTime LastSuccessfulRegistration => _lastSuccessfulRegistration;
    public IReadOnlyDictionary<string, long> Metrics => _metrics;

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

            // STUN gives us the public IP, but nothing consumes it unless we also set ContactHost.
            // Without that, REGISTER Contact stays on the private LAN address (e.g. 192.168.x.x).
            // Many PBXs (VitalPBX/Asterisk) then store that Contact, fail qualify/OPTIONS against
            // it, and treat the AOR as unreachable — callers hear busy and we never see an INVITE.
            // RTP-side NAT fixes (AcceptRtpFromAny + keep-alive) only help after a call is up.
            StunHelper.SetupStun();
            if (!string.IsNullOrWhiteSpace(StunHelper.PublicIPAddress))
            {
                _sipTransport.ContactHost = StunHelper.PublicIPAddress;
                Log.Information(
                    $"SIP ContactHost set to STUN public IP {StunHelper.PublicIPAddress} for NAT traversal");
            }
            else
            {
                Log.Warning(
                    "STUN public IP unresolved; REGISTER Contact will use the local bind address " +
                    "(inbound calls may fail behind NAT)");
            }

            // SIPSorcery's default REGISTER Contact is "sip:host:port" with no user part. VitalPBX
            // is happier with "sip:ext@host:port" (matches the AOR). Customise after ContactHost
            // so we own the final URI; return the header to replace.
            _sipTransport.CustomiseRequestHeader = (localEP, remoteEP, req) =>
            {
                if (req.Method != SIPMethodsEnum.REGISTER)
                    return null!;

                string host = !string.IsNullOrWhiteSpace(StunHelper.PublicIPAddress)
                    ? StunHelper.PublicIPAddress
                    : localEP.Address.ToString();
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
        if (req.Method != SIPMethodsEnum.OPTIONS)
            return Task.CompletedTask;

        try
        {
            var optionsResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
            Log.Debug($"Answering OPTIONS from {remoteEP} with 200 OK");
            return _sipTransport.SendResponseAsync(optionsResponse);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to answer OPTIONS from {remoteEP}");
            return Task.CompletedTask;
        }
    }

    private void InitializeRegistrationAgent()
    {
        try
        {
            _registrationAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _sipUsername,
                _sipPassword,
                _sipServer,
                _registrationExpirySeconds
            );

            _registrationAgent.RegistrationSuccessful += OnRegistrationSuccessful;
            _registrationAgent.RegistrationFailed += OnRegistrationFailed;
            _registrationAgent.RegistrationTemporaryFailure += OnRegistrationTemporaryFailure;
            _registrationAgent.RegistrationRemoved += OnRegistrationRemoved;

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
        return userAgent;
    }

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
                _lastRegistrationAttempt = DateTime.UtcNow;
                _registrationAgent.Start();
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

        try
        {
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

            _registrationAgent?.Stop();
            
            StopHealthMonitoring();
            StopReconnectionTimer();
            
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

    // Event handlers
    private void OnRegistrationSuccessful(SIPURI uri, SIPResponse resp)
    {
        lock (_lockObject)
        {
            _isRegistered = true;
            _reconnectionAttempts = 0;
            _lastSuccessfulRegistration = DateTime.UtcNow;
        }

        StatusMessage?.Invoke(this, $"Registration successful for {uri}. Expires: {resp.Header.Expires}");
        RegistrationStatusChanged?.Invoke(this);
        IncrementMetric("successful_registrations");
        Log.Debug($"SIP registration successful for {uri}");
    }

    private void OnRegistrationFailed(SIPURI uri, SIPResponse resp, string error)
    {
        lock (_lockObject)
        {
            _isRegistered = false;
        }

        StatusMessage?.Invoke(this, $"Registration failed for {uri}: {error}");
        RegistrationStatusChanged?.Invoke(this);
        IncrementMetric("failed_registrations");
        Log.Warning($"SIP registration failed for {uri}: {error}");

        if (_enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnRegistrationTemporaryFailure(SIPURI uri, SIPResponse resp, string error)
    {
        StatusMessage?.Invoke(this, $"Registration temporary failure for {uri}: {error}");
        IncrementMetric("temporary_registration_failures");
        Log.Warning($"SIP registration temporary failure for {uri}: {error}");

        if (_enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnRegistrationRemoved(SIPURI uri, SIPResponse resp)
    {
        lock (_lockObject)
        {
            _isRegistered = false;
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
        StatusMessage?.Invoke(this, "Call failed: " + errorMessage + ".");
        IncrementMetric("call_failures");
        CallFinished(null);
        Log.Warning($"Call failed: {errorMessage}");
    }

    private void CallAnswered(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call answered: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        CallAnswer?.Invoke(this);
        IncrementMetric("calls_answered");
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
        _healthCheckTimer = new Timer(PerformHealthCheck, null, HealthCheckIntervalMs, HealthCheckIntervalMs);
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
            
            // If we haven't registered successfully in the last 2 minutes, try to re-register.
            // Route through ScheduleReconnection() so this respects the same MaxReconnectionAttempts/
            // backoff as registration-failure driven reconnects, instead of hammering the server with
            // an uncapped StartRegistration() call every HealthCheckIntervalMs.
            if (timeSinceLastRegistration.TotalMinutes > 2 && !_isRegistered)
            {
                Log.Warning("Health check: No successful registration in 2+ minutes, scheduling re-registration");
                if (_enableAutoReconnection)
                {
                    ScheduleReconnection();
                }
                else
                {
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

    private void ScheduleReconnection()
    {
        lock (_lockObject)
        {
            if (_reconnectionAttempts >= MaxReconnectionAttempts)
            {
                Log.Error($"Maximum reconnection attempts ({MaxReconnectionAttempts}) reached");
                return;
            }

            _reconnectionAttempts++;
            var delay = ReconnectionDelayMs * _reconnectionAttempts; // Exponential backoff

            _reconnectionTimer?.Dispose();
            _reconnectionTimer = new Timer(AttemptReconnection, null, delay, Timeout.Infinite);
            
            Log.Information($"Scheduling reconnection attempt {_reconnectionAttempts} in {delay}ms");
        }
    }

    private void StopReconnectionTimer()
    {
        _reconnectionTimer?.Dispose();
        _reconnectionTimer = null;
    }

    private void AttemptReconnection(object? state)
    {
        try
        {
            Log.Information($"Attempting reconnection (attempt {_reconnectionAttempts})");
            StartRegistration();
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
