using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.SIP;
using SipBot;

namespace SipBotLib.Tests;

/// <summary>
/// A SipClient registered against a <see cref="FakeRegistrar"/>, with timings shortened so the
/// registration tests run in seconds: dropped REGISTERs fail after 2 s, the registration agent's
/// own retry is 2 s, default attempts are 100 ms × n, and extended retry doubles from 100 ms to
/// 400 ms.
/// </summary>
internal sealed class RegistrationTestHarness : IDisposable
{
    public FakeRegistrar Registrar { get; } = new();
    public SipClient Client { get; }
    public ConcurrentQueue<Exception> Errors { get; } = new();
    private int _statusChanges;
    public int StatusChanges => Volatile.Read(ref _statusChanges);

    public RegistrationTestHarness(
        bool extended = false,
        bool autoReconnect = true,
        bool healthMonitoring = false,
        Action<RegistrationRetryOptions>? tune = null)
    {
        var transport = new SIPTransport();
        transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));

        var config = new SipConfig { Server = Registrar.Server, Username = "101", Password = "secret" };
        var retry = config.RegistrationRetry;
        retry.Extended = extended;
        retry.AgentAttemptTimeoutSeconds = 2;
        retry.AgentFailureRetrySeconds = 2;
        retry.DefaultBaseDelayMs = 100;
        retry.ExtendedInitialDelayMs = 100;
        retry.ExtendedMaxDelayMs = 400;
        retry.HealthCheckIntervalMs = 200;
        retry.HealthCheckStaleMs = 300;
        tune?.Invoke(retry);

        // Expires 30 is SipClient's minimum; the agent refreshes 5 s before expiry (every 25 s).
        Client = new SipClient(transport, config, registrationExpirySeconds: 30,
            enableAutoReconnection: autoReconnect, enableHealthMonitoring: healthMonitoring);
        Client.ErrorOccurred += (_, ex) => Errors.Enqueue(ex);
        Client.RegistrationStatusChanged += _ => Interlocked.Increment(ref _statusChanges);
    }

    public bool SawAlreadyRunning => Errors.Any(e => e.Message.Contains("already running"));

    public long Metric(string name) => Client.Metrics.TryGetValue(name, out var value) ? value : 0;

    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }

    public Task<bool> WaitForState(RegistrationState state, TimeSpan timeout) =>
        WaitUntil(() => Client.RegistrationState == state, timeout);

    /// <summary>Gaps in milliseconds between consecutive REGISTERs, from index <paramref name="from"/>.</summary>
    public double[] GapsMs(int from = 0)
    {
        var at = Registrar.Registers.Select(r => r.At.TotalMilliseconds).ToArray();
        return Enumerable.Range(from + 1, Math.Max(0, at.Length - from - 1)).Select(i => at[i] - at[i - 1]).ToArray();
    }

    public void Dispose()
    {
        Client.Dispose();
        Registrar.Dispose();
    }
}
