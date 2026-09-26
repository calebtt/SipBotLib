using System.Diagnostics;
using SipBot;
using Xunit;
using static SipBotLib.Tests.FakeRegistrar;

namespace SipBotLib.Tests;

/// <summary>
/// Shutdown unregisters (REGISTER Expires 0, answering a digest challenge if one comes) before the
/// transport closes, waits at most <see cref="SipClient.UnregisterTimeout"/> for it, and schedules
/// no reconnection afterwards.
/// </summary>
public class ShutdownUnregisterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_client_unregisters_before_the_transport_closes(bool challengeFirst)
    {
        using var h = new RegistrationTestHarness();
        h.Registrar.ChallengeFirst = challengeFirst;
        h.Registrar.Script = Reply.Ok;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(5)));

        var elapsed = Stopwatch.StartNew();
        h.Client.Dispose();
        elapsed.Stop();

        var last = h.Registrar.Registers[^1];
        Assert.Equal(0, last.Expires);
        Assert.Equal(challengeFirst, last.Authenticated);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"shutdown took {elapsed.Elapsed}");

        // RegistrationRemoved must not trigger a reconnect on a client that is shutting down.
        int count = h.Registrar.Count;
        await Task.Delay(1000);
        Assert.Equal(count, h.Registrar.Count);
    }

    [Fact]
    public async Task Unconfirmed_unregister_is_bounded_by_the_timeout()
    {
        using var h = new RegistrationTestHarness();
        h.Registrar.Script = Reply.Ok;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.Registered, TimeSpan.FromSeconds(5)));
        h.Registrar.Script = Reply.Drop;

        var elapsed = Stopwatch.StartNew();
        h.Client.Dispose();
        elapsed.Stop();

        Assert.Equal(0, h.Registrar.Registers[^1].Expires);
        Assert.InRange(elapsed.Elapsed, SipClient.UnregisterTimeout - TimeSpan.FromMilliseconds(300),
            SipClient.UnregisterTimeout + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Unregistered_client_sends_nothing_on_shutdown()
    {
        using var h = new RegistrationTestHarness();
        h.Registrar.Script = Reply.Forbidden;
        h.Client.StartRegistration();
        Assert.True(await h.WaitForState(RegistrationState.HardFailure, TimeSpan.FromSeconds(5)));
        int count = h.Registrar.Count;

        var elapsed = Stopwatch.StartNew();
        h.Client.Dispose();
        elapsed.Stop();

        await Task.Delay(500);
        Assert.Equal(count, h.Registrar.Count);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"shutdown took {elapsed.Elapsed}");
    }
}
