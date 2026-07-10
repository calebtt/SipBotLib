using Serilog;
using SipBot;
using SIPSorcery.SIP;
using System.Diagnostics;

// Live-call harness for SipBotLib PR #5 (audio start/stop, RTP durations, pacer).
// Registers as a SIP extension, auto-answers, echoes inbound PCM (plus a short tone
// on answer so far-end hears something even before they speak).

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var positional = args.Where(a => !a.StartsWith("--")).ToList();
string settingsPath = positional.Count > 0 ? positional[0] : "sipsettings.json";
bool enableKeepAlive = !args.Any(a => a is "--no-keepalive");
bool enableWideband = !args.Any(a => a is "--pcmu-only");
int configIndex = 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--config" && int.TryParse(args[i + 1], out var idx))
        configIndex = idx;
}

if (!File.Exists(settingsPath))
{
    var candidates = new[]
    {
        settingsPath,
        Path.Combine(AppContext.BaseDirectory, "sipsettings.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "sipsettings.json"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sipsettings.json")),
    };
    settingsPath = candidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException(
            "sipsettings.json not found. Pass path as first arg, or place next to cwd.");
}

SipBotSettings.LoadSettingsFromJson(settingsPath);
var configs = SipBotSettings.Settings.Configs;
if (configIndex < 0 || configIndex >= configs.Count)
    throw new InvalidOperationException($"--config {configIndex} out of range (0..{configs.Count - 1})");

var cfg = configs[configIndex];
Log.Information(
    "Starting LiveCallTest as {User}@{Server} keepAlive={KA} wideband={WB}",
    cfg.Username, cfg.Server, enableKeepAlive, enableWideband);

var transport = new SIPTransport();
transport.AddSIPChannel(new SIPUDPChannel(System.Net.IPAddress.Any, 0));
// Trace REGISTER Contact so we can confirm NAT public IP made it into the AOR
transport.SIPRequestOutTraceEvent += (_, ep, req) =>
{
    if (req.Method == SIPMethodsEnum.REGISTER)
    {
        var contact = req.Header.Contact?.FirstOrDefault()?.ToString() ?? "(none)";
        Log.Information("[sip-out] REGISTER -> {Ep} Contact={Contact}", ep, contact);
    }
};

using var client = new SipClient(transport, cfg, registrationExpirySeconds: 120);
var endpoint = new EchoAudioEndPoint(enableKeepAlive, enableWideband);
var answerLock = new SemaphoreSlim(1, 1);

client.StatusMessage += (_, msg) => Log.Information("[status] {Msg}", msg);
client.RegistrationStatusChanged += c =>
    Log.Information("[reg] IsRegistered={R} lastOk={T:u}", c.IsRegistered, c.LastSuccessfulRegistration);
client.ErrorOccurred += (_, ex) => Log.Error(ex, "[error]");
client.CallEnded += _ =>
{
    Log.Information(
        "[call] ended. inboundFrames={N} lastInboundRate={R}Hz durationRx={D:mm\\:ss}",
        endpoint.InboundFrameCount, endpoint.LastInboundSampleRateHz, endpoint.InboundAudioDuration);
    endpoint.ResetStats();
};
client.CallDurationUpdated += (_, d) => Log.Information("[call] duration {D:mm\\:ss}", d);
client.CallAnswer += _ => Log.Information("[call] CallAnswer event (media up)");

client.IncomingCall += async (c, invite) =>
{
    if (!await answerLock.WaitAsync(0).ConfigureAwait(false))
    {
        Log.Warning("[call] already handling a call, ignoring INVITE");
        return;
    }

    try
    {
        if (c.IsCallActive)
        {
            Log.Warning("[call] already active, ignoring INVITE");
            return;
        }

        Log.Information("[sip] INVITE from {From} call-id={Id}", invite.Header.From, invite.Header.CallId);
        c.Accept(invite);
        Log.Information("[call] Accept() done, answering…");
        bool ok = await c.Answer(endpoint, endpoint).ConfigureAwait(false);
        Log.Information(
            "[call] Answer() => {Ok}. Speak into the phone; you should hear echo + answer tone.", ok);
        if (ok)
            endpoint.PlayAnswerTone();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[call] failed to accept/answer");
    }
    finally
    {
        // Keep lock until call ends so we don't double-answer
        // Release when call ends via CallEnded
    }
};

client.CallEnded += _ =>
{
    try { answerLock.Release(); } catch (SemaphoreFullException) { }
};

client.StartRegistration();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Log.Information("Listener starting. From phone, call extension {Ext}. Ctrl+C to quit.", cfg.Username);
Log.Information("Flags: keep-alive={KA}, wideband(G.722 prefer)={WB}", enableKeepAlive, enableWideband);

var lastStatsLog = Stopwatch.StartNew();
try
{
    while (!cts.IsCancellationRequested)
    {
        if (client.IsCallActive && lastStatsLog.ElapsedMilliseconds >= 5000)
        {
            lastStatsLog.Restart();
            Log.Information(
                "[stats] inboundFrames={N} lastRate={R}Hz rxMs≈{Ms}",
                endpoint.InboundFrameCount,
                endpoint.LastInboundSampleRateHz,
                (int)endpoint.InboundAudioDuration.TotalMilliseconds);
        }

        await Task.Delay(200, cts.Token).ConfigureAwait(false);
    }
}
catch (OperationCanceledException)
{
    Log.Information("Shutting down…");
}
finally
{
    if (client.IsCallActive)
        client.Hangup();
    transport.Shutdown();
    Log.CloseAndFlush();
}

/// <summary>
/// Minimal BaseAudioEndPoint: echoes inbound PCM and can play a short answer tone.
/// </summary>
sealed class EchoAudioEndPoint : BaseAudioEndPoint
{
    private long _inboundFrames;
    private long _inboundSamples;
    private int _lastRate;

    public long InboundFrameCount => Interlocked.Read(ref _inboundFrames);
    public int LastInboundSampleRateHz => _lastRate;
    public TimeSpan InboundAudioDuration =>
        _lastRate > 0
            ? TimeSpan.FromSeconds(Interlocked.Read(ref _inboundSamples) / (double)_lastRate)
            : TimeSpan.Zero;

    public EchoAudioEndPoint(bool keepAlive, bool wideband)
        : base(enableContinuousKeepAlive: keepAlive, enableWidebandAudio: wideband)
    {
    }

    public override Task InitializeAsync() => Task.CompletedTask;
    public override Task ShutdownAsync() => Task.CompletedTask;

    public void ResetStats()
    {
        Interlocked.Exchange(ref _inboundFrames, 0);
        Interlocked.Exchange(ref _inboundSamples, 0);
        _lastRate = 0;
    }

    public void PlayAnswerTone()
    {
        // 500ms of 440Hz at 8 kHz — SendAudioFrame resamples/encodes as needed
        const int rate = 8000;
        const double secs = 0.5;
        int n = (int)(rate * secs);
        var pcm = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        int frameSamples = rate / 50;
        int frameBytes = frameSamples * 2;
        for (int off = 0; off + frameBytes <= pcm.Length; off += frameBytes)
        {
            var chunk = new byte[frameBytes];
            Buffer.BlockCopy(pcm, off, chunk, 0, frameBytes);
            SendAudioFrame(chunk, rate);
        }
        Log.Information("[audio] answer tone enqueued ({Ms}ms)", (int)(secs * 1000));
    }

    protected override Task ProcessAudioAsync(byte[] pcm, int sampleRateHz)
    {
        Interlocked.Increment(ref _inboundFrames);
        Interlocked.Add(ref _inboundSamples, pcm.Length / 2);
        _lastRate = sampleRateHz;

        // Echo back — exercises outbound encode path (PCMU or G.722) + RTP duration math
        if (pcm.Length >= 2)
            SendAudioFrame(pcm, sampleRateHz);

        return Task.CompletedTask;
    }
}
