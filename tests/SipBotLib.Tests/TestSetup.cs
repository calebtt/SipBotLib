using System.Runtime.CompilerServices;

namespace SipBotLib.Tests;

internal static class TestSetup
{
    /// <summary>
    /// SIPSorcery blocks thread-pool threads (each registration attempt waits synchronously for
    /// its response). With many test classes running in parallel on a small CI runner, the pool
    /// runs dry and grows by about one thread a second, so timers fire up to ~1 s late and the
    /// registration timing tests fail. A higher minimum keeps timers on time.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }
}
