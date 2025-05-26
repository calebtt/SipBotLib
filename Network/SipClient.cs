using Serilog;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
namespace SipBot;

public class SipClient
{
    private const int RegistrationExpirySeconds = 60;

    private readonly string _sipUsername;
    private readonly string _sipPassword;
    private readonly string _sipServer;
    private readonly string _sipFromName;

    private SIPTransport _sipTransport;
    private SIPUserAgent _userAgent;
    private SIPServerUserAgent? _pendingIncomingCall;

    public event Action<SipClient>? CallAnswer;
    public event Action<SipClient>? CallEnded;
    public event Action<SipClient, string>? StatusMessage;
    public event Action<SipClient>? RemotePutOnHold;
    public event Action<SipClient>? RemoteTookOffHold;

    private VoIPMediaSession? _mediaSession = null;
    private SIPRegistrationUserAgent _registrationAgent;

    public SipClient(
    SIPTransport sipTransport,
    SipConfig sipSettings)
    {
        _sipTransport = sipTransport;
        _sipUsername = sipSettings.Username;
        _sipPassword = sipSettings.Password;
        _sipServer = sipSettings.Server;
        _sipFromName = sipSettings.FromName;

        _sipTransport.SIPRequestInTraceEvent += (localSIPEndPoint, endPoint, request) =>
            Log.Debug($"SIP Request Received: {request.Method} from {endPoint}");

        _sipTransport.SIPResponseOutTraceEvent += (localSIPEndPoint, endPoint, response) =>
            Log.Debug($"SIP Response Sent: {response.Status} to {endPoint}");

        StunHelper.SetupStun();

        // Initialize registration agent
        _registrationAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            _sipUsername,
            _sipPassword,
            _sipServer,
            RegistrationExpirySeconds
        );

        _userAgent = CreateNewUserAgent(_sipTransport);

        _registrationAgent.RegistrationSuccessful += (uri, resp) =>
            StatusMessage?.Invoke(this, $"Registration successful for {uri}. Expires: {resp.Header.Expires}");
        _registrationAgent.RegistrationFailed += (uri, resp, error) =>
            StatusMessage?.Invoke(this, $"Registration failed for {uri}: {error}");
        _registrationAgent.RegistrationTemporaryFailure += (uri, resp, error) =>
            StatusMessage?.Invoke(this, $"Registration temporary failure for {uri}: {error}");

    }

    public void StartRegistration()
    {
        _registrationAgent.Start();
        StatusMessage?.Invoke(this, $"Registration attempt for {_sipUsername}@{_sipServer} started.");
    }

    public void Shutdown()
    {
        Hangup();
        _mediaSession?.Dispose();
        _mediaSession = null;
        _registrationAgent?.Stop();
        _sipTransport.Shutdown();
    }

    public void Accept(SIPRequest sipRequest)
    {
        _pendingIncomingCall = _userAgent.AcceptCall(sipRequest);
    }

    public async Task<bool> Answer(IAudioSink audioSink, IAudioSource audioSource)
    {
        if (_pendingIncomingCall == null)
        {
            StatusMessage?.Invoke(this, $"There was no pending call available to answer.");
            return false;
        }
        else
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
                CallAnswer?.Invoke(this);
            }

            return result;
        }
    }

    public void Hangup()
    {
        if (_userAgent.IsCallActive)
        {
            _userAgent.Hangup();
            CallFinished(null);
            CallEnded?.Invoke(this);
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

    private SIPUserAgent CreateNewUserAgent(SIPTransport sipTransport)
    {
        var userAgent = new SIPUserAgent(sipTransport, null);
        userAgent.ClientCallTrying += CallTrying;
        userAgent.ClientCallRinging += CallRinging;
        userAgent.ClientCallAnswered += CallAnswered;
        userAgent.ClientCallFailed += CallFailed;
        userAgent.OnCallHungup += CallFinished;
        userAgent.ServerCallCancelled += IncomingCallCancelled;
        return userAgent;
    }

    private void CallTrying(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call trying: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
    }

    private void CallRinging(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call ringing: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
    }

    private void CallFailed(ISIPClientUserAgent uac, string errorMessage, SIPResponse? failureResponse)
    {
        StatusMessage?.Invoke(this, "Call failed: " + errorMessage + ".");
        CallFinished(null);
    }

    private void CallAnswered(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call answered: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        CallAnswer?.Invoke(this);
    }

    private void CallFinished(SIPDialogue? dialogue)
    {
        _mediaSession?.Close("Call Finished");
        //_mediaSession?.Dispose();
        _mediaSession = null;
        _pendingIncomingCall = null;
        CallEnded?.Invoke(this);
    }

    private void IncomingCallCancelled(ISIPServerUserAgent uas, SIPRequest cancelRequest)
    {
        CallFinished(null);
    }
}
