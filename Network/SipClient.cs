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

            StunHelper.SetupStun();
            IncrementMetric("transport_initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SIP transport");
            ErrorOccurred?.Invoke(this, ex);
            throw;
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
            _pendingIncomingCall = _userAgent.AcceptCall(sipRequest);
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

        if (_pendingIncomingCall == null)
        {
            StatusMessage?.Invoke(this, "There was no pending call available to answer.");
            return false;
        }

        try
        {
            var sipRequest = _pendingIncomingCall.ClientTransaction.TransactionRequest;

            bool hasAudio = true;
            bool hasVideo = false;

            if (sipRequest.Body != null)
            {
                SDP offerSDP = SDP.ParseSDPDescription(sipRequest.Body);
                hasAudio = offerSDP.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.MediaStreamStatus != MediaStreamStatusEnum.Inactive);
                hasVideo = offerSDP.Media.Any(x => x.Media == SDPMediaTypesEnum.video && x.MediaStreamStatus != MediaStreamStatusEnum.Inactive);
            }

            _mediaSession = CreateMediaSession(CreateMediaEndPoints(audioSink, audioSource));

            bool result = await _userAgent.Answer(_pendingIncomingCall, _mediaSession);
            _pendingIncomingCall = null;

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
                _userAgent.Hangup();
                CallFinished(null);
                CallEnded?.Invoke(this);
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

    public void Shutdown()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            Log.Information("Shutting down SIP client");
            
            Hangup();
            
            _mediaSession?.Close("Shutdown");
            _mediaSession?.Dispose();
            _mediaSession = null;
            
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

        _mediaSession?.Close("Call Finished");
        _mediaSession?.Dispose();
        _mediaSession = null;
        _pendingIncomingCall = null;
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
            
            // If we haven't registered successfully in the last 2 minutes, try to re-register
            if (timeSinceLastRegistration.TotalMinutes > 2 && !_isRegistered)
            {
                Log.Warning("Health check: No successful registration in 2+ minutes, attempting re-registration");
                StartRegistration();
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

        _isDisposed = true;
        Shutdown();
        
        _healthCheckTimer?.Dispose();
        _reconnectionTimer?.Dispose();
        
        Log.Information("SIP Client disposed");
    }
}
