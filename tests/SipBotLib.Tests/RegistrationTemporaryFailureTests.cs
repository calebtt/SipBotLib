using SipBot;
using Xunit;
using static SipBotLib.Tests.FakeRegistrar;

namespace SipBotLib.Tests;

/// <summary>
/// Default retry after a temporary failure: at most five attempts at base × n, no "already
/// running", then the registration agent's own retry is still armed.
/// </summary>
public class RegistrationDefaultRetryTests
{
    [Fact]
    public async Task Five_attempts_at_base_times_n_then_the_agent_retry_remains()
    {
        using var h = new RegistrationTestHarness();
        h.Registrar.Script = Reply.ServiceUnavailable;

        h.Client.StartRegistration();

        // Initial REGISTER + 5 client attempts + 1 from the agent's own 2 s retry.
        Assert.True(await h.Registrar.WaitForCountAsync(7, TimeSpan.FromSeconds(15)), $"got {h.Registrar.Count}");
        double[] gaps = h.GapsMs();
        for (int n = 1; n <= 5; n++)
        {
            double expected = 100 * n;
            Assert.InRange(gaps[n - 1], expected * 0.8, expected + 400);
        }
        Assert.InRange(gaps[5], 1600, 3500);
        Assert.False(h.Client.IsRegistered);
        Assert.Equal(RegistrationState.TemporaryFailure, h.Client.RegistrationState);
        Assert.StartsWith("503", h.Client.LastRegistrationError);
        Assert.False(h.SawAlreadyRunning);
    }

    [Fact]
    public async Task Dispose_while_a_reconnect_is_pending_sends_nothing_more()
    {
        using var h = new RegistrationTestHarness(tune: r => r.DefaultBaseDelayMs = 1000);
        h.Registrar.Script = Reply.ServiceUnavailable;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.TemporaryFailure, TimeSpan.FromSeconds(5)));

        h.Client.Dispose();
        int count = h.Registrar.Count;
        await Task.Delay(2500);

        Assert.Equal(count, h.Registrar.Count);
        Assert.Empty(h.Errors);
    }
}

/// <summary>
/// A temporary failure after a successful registration clears the registered flag and reports
/// the change, with or without auto-reconnect, and registration comes back without "already
/// running". Waits for a real refresh (25 s), so it is its own class.
/// </summary>
public class RegistrationTemporaryFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Timeout_after_success_clears_the_flag_and_recovers(bool autoReconnect)
    {
        using var h = new RegistrationTestHarness(autoReconnect: autoReconnect, healthMonitoring: !autoReconnect);
        h.Registrar.Script = Reply.Ok;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(5)));
        var lastSuccess = h.Client.LastSuccessfulRegistration;
        int changes = h.StatusChanges;

        // Drop the refresh REGISTER (25 s after registering). The agent gives up after 2 s.
        h.Registrar.Script = Reply.Drop;
        Assert.True(await h.WaitForState(RegistrationState.TemporaryFailure, TimeSpan.FromSeconds(35)),
            $"state is {h.Client.RegistrationState}");
        Assert.False(h.Client.IsRegistered);
        Assert.True(h.StatusChanges > changes);
        Assert.NotNull(h.Client.LastRegistrationError);
        Assert.Equal(lastSuccess, h.Client.LastSuccessfulRegistration);

        // Recovery: a client attempt (auto-reconnect on) or the health check (off).
        h.Registrar.Script = Reply.Ok;
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(10)));
        Assert.True(h.Client.IsRegistered);
        Assert.False(h.SawAlreadyRunning);
        Assert.Empty(h.Errors);

        // After a hard failure the health check must not start registration again.
        h.Registrar.Script = Reply.Forbidden;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(5)));
        int count = h.Registrar.Count;
        await Task.Delay(1500);
        Assert.Equal(count, h.Registrar.Count);
    }
}

/// <summary>
/// Extended retry: the delay doubles to the cap and retries never stop; the health check cannot
/// add REGISTERs on top; a hard failure still stops everything.
/// </summary>
public class RegistrationExtendedRetryTests
{
    [Fact]
    public async Task Delay_doubles_to_the_cap_and_retries_do_not_stop()
    {
        using var h = new RegistrationTestHarness(extended: true);
        h.Registrar.Script = Reply.ServiceUnavailable;

        h.Client.StartRegistration();
        await Task.Delay(TimeSpan.FromSeconds(8)); // 20 × the 400 ms cap

        double[] gaps = h.GapsMs();
        Assert.True(gaps.Length >= 12, $"only {gaps.Length + 1} REGISTERs in 8 s");
        Assert.InRange(gaps[0], 80, 500);
        Assert.InRange(gaps[1], 160, 600);
        foreach (double gap in gaps.Skip(2))
            Assert.InRange(gap, 320, 800);
        Assert.False(h.SawAlreadyRunning);

        h.Registrar.Script = Reply.Ok;
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Health_check_adds_no_REGISTERs_and_a_hard_failure_stops_retries()
    {
        using var h = new RegistrationTestHarness(extended: true, healthMonitoring: true,
            tune: r => { r.HealthCheckIntervalMs = 50; r.HealthCheckStaleMs = 100; });
        h.Registrar.Script = Reply.ServiceUnavailable;

        h.Client.StartRegistration();
        await Task.Delay(2000);

        // One REGISTER in flight at a time: never closer together than the first backoff step.
        Assert.All(h.GapsMs(), gap => Assert.True(gap >= 80, $"gap {gap} ms"));

        h.Registrar.Script = Reply.Forbidden;
        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(3)));
        int count = h.Registrar.Count;
        await Task.Delay(1500);
        Assert.Equal(count, h.Registrar.Count);
        Assert.False(h.SawAlreadyRunning);
    }
}

/// <summary>
/// The health check leaves an attempt in flight alone. Found live: with REGISTERs being dropped,
/// the health check (auto-reconnect on) scheduled the next attempt 3 s into the current one, which
/// stopped the in-flight agent and skipped a backoff step. Here every REGISTER is dropped, each
/// attempt waits 2 s for a response, and the health check fires every 100 ms.
/// </summary>
public class RegistrationHealthCheckInFlightTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_check_does_not_interrupt_an_attempt_in_flight(bool extended)
    {
        using var h = new RegistrationTestHarness(extended: extended, healthMonitoring: true,
            tune: r => { r.HealthCheckIntervalMs = 100; r.HealthCheckStaleMs = 100; });
        h.Registrar.Script = Reply.Drop;

        h.Client.StartRegistration();
        await Task.Delay(TimeSpan.FromSeconds(7));

        // Each attempt runs its full 2 s before the next REGISTER; the health check adds none.
        double[] gaps = h.GapsMs();
        Assert.True(gaps.Length >= 2, $"only {gaps.Length + 1} REGISTERs in 7 s");
        Assert.All(gaps, gap => Assert.True(gap >= 1800, $"gap {gap} ms: an attempt was cut short"));
        Assert.False(h.SawAlreadyRunning);
    }
}

/// <summary>
/// After a success the extended backoff starts over: the first retry of the next outage comes
/// after the initial delay again. Waits for a real refresh (25 s), so it is its own class.
/// </summary>
public class RegistrationExtendedRetryResetTests
{
    [Fact]
    public async Task Backoff_restarts_after_a_successful_registration()
    {
        using var h = new RegistrationTestHarness(extended: true);
        h.Registrar.Script = Reply.ServiceUnavailable;
        h.Client.StartRegistration();
        await Task.Delay(1500); // backoff climbs to the 400 ms cap
        h.Registrar.Script = Reply.Ok;
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(3)));

        // Second outage at the next refresh (25 s later).
        h.Registrar.Script = Reply.ServiceUnavailable;
        int before = h.Registrar.Count;
        Assert.True(await h.Registrar.WaitForCountAsync(before + 2, TimeSpan.FromSeconds(35)));

        double firstRetryGap = h.GapsMs(before)[0];
        Assert.InRange(firstRetryGap, 80, 350);
    }
}
