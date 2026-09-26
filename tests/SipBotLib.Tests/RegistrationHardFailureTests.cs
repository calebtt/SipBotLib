using SipBot;
using Xunit;
using static SipBotLib.Tests.FakeRegistrar;

namespace SipBotLib.Tests;

/// <summary>
/// A hard failure (402/403/404, or 401 after authentication) stops the registration agent: no
/// further REGISTER until StartRegistration() is called, whatever the retry mode.
/// </summary>
public class RegistrationHardFailureTests
{
    public static TheoryData<Reply, bool, string, bool> HardFailures()
    {
        var data = new TheoryData<Reply, bool, string, bool>();
        foreach (bool extended in new[] { false, true })
        {
            // On the first REGISTER. SIPSorcery reports the 402 as a temporary failure.
            data.Add(Reply.Forbidden, false, "403", extended);
            data.Add(Reply.NotFound, false, "404", extended);
            data.Add(Reply.PaymentRequired, false, "402", extended);
            // On the authenticated REGISTER, after a 401 challenge.
            data.Add(Reply.Forbidden, true, "403", extended);
            data.Add(Reply.NotFound, true, "404", extended);
            data.Add(Reply.PaymentRequired, true, "402", extended);
            data.Add(Reply.Unauthorized, true, "401", extended);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(HardFailures))]
    public async Task Hard_failure_stops_registration(Reply reply, bool challengeFirst, string status, bool extended)
    {
        using var h = new RegistrationTestHarness(extended: extended, healthMonitoring: true);
        h.Registrar.ChallengeFirst = challengeFirst;
        h.Registrar.Script = reply;

        h.Client.StartRegistration();

        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(5)),
            $"state is {h.Client.RegistrationState}");
        int count = h.Registrar.Count;
        await Task.Delay(1500);

        Assert.Equal(count, h.Registrar.Count);
        Assert.False(h.Client.IsRegistered);
        Assert.StartsWith(status, h.Client.LastRegistrationError);
        Assert.True(h.StatusChanges > 0);
        Assert.False(h.SawAlreadyRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRegistration_re_registers_after_a_hard_failure(bool extended)
    {
        using var h = new RegistrationTestHarness(extended: extended);
        h.Registrar.Script = Reply.Ok;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(5)));
        var lastSuccess = h.Client.LastSuccessfulRegistration;

        // 200, then the registrar starts refusing the account.
        h.Registrar.Script = Reply.Forbidden;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(5)));
        Assert.Equal(lastSuccess, h.Client.LastSuccessfulRegistration);
        Assert.StartsWith("403", h.Client.LastRegistrationError);

        // Re-registering while the 403 persists: one REGISTER, then silence again. Each Start()
        // arms a fresh repeating timer, so the client has to stop the agent again.
        int before = h.Registrar.Count;
        h.Client.StartRegistration();
        Assert.True(await h.Registrar.WaitForCountAsync(before + 1, TimeSpan.FromSeconds(5)));
        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(5)));
        await Task.Delay(1500);
        Assert.Equal(before + 1, h.Registrar.Count);

        // Once the account is accepted again, StartRegistration() recovers.
        h.Registrar.Script = Reply.Ok;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(5)));
        Assert.True(h.Client.IsRegistered);
        Assert.False(h.SawAlreadyRunning);
    }
}

/// <summary>
/// The case that makes stopping the agent necessary: a hard failure on the first REGISTER after
/// Start() leaves SIPSorcery's repeating timer (period Expires - 5 s = 25 s here) running, which
/// would send the rejected credentials again. Separate class so xUnit runs it in parallel.
/// </summary>
public class RegistrationHardFailureRepeatingTimerTests
{
    [Fact]
    public async Task No_REGISTER_for_a_full_refresh_period_after_a_hard_failure_on_the_first_REGISTER()
    {
        using var h = new RegistrationTestHarness();
        h.Registrar.Script = Reply.Forbidden;

        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, h.Registrar.Count);

        await Task.Delay(TimeSpan.FromSeconds(28));

        Assert.Equal(1, h.Registrar.Count);
    }
}
