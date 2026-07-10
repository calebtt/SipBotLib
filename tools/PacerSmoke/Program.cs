using SipBot;
using System.Diagnostics;

// Smoke tests for PR #5 pacer semantics (no live SIP required).
int failures = 0;
void Check(string name, bool cond)
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}: {name}");
    if (!cond) failures++;
}

// --- IsPlaying ignores silence, tracks real frames ---
{
    var pacer = new RtpPacedSender();
    bool complete = false;
    pacer.SendingComplete += () => complete = true;
    var sent = new List<byte[]>();
    pacer.SendAction = (_, frame) => sent.Add(frame);
    pacer.Start();

    Check("IsPlaying false when empty", !pacer.IsPlaying);

    // silence frame (all 0x7F) should not mark playing
    var silence = Enumerable.Repeat((byte)0x7F, 160).ToArray();
    pacer.Enqueue(silence);
    await Task.Delay(5);
    Check("IsPlaying false after silence enqueue", !pacer.IsPlaying);

    // real audio
    var real = Enumerable.Repeat((byte)0x10, 160).ToArray();
    pacer.Enqueue(real);
    pacer.Enqueue(real);
    Check("IsPlaying true with real frames queued", pacer.IsPlaying);

    // wait for drain
    var sw = Stopwatch.StartNew();
    while (pacer.IsPlaying && sw.ElapsedMilliseconds < 3000)
        await Task.Delay(20);
    Check("IsPlaying false after real frames sent", !pacer.IsPlaying);
    Check("SendingComplete fired after real audio", complete);

    await pacer.Stop();
}

// --- Silence after real audio should still complete ---
{
    var pacer = new RtpPacedSender();
    int completeCount = 0;
    pacer.SendingComplete += () => completeCount++;
    pacer.SendAction = (_, _) => { };
    pacer.Start();

    var real = Enumerable.Repeat((byte)0x22, 160).ToArray();
    var silence = Enumerable.Repeat((byte)0x7F, 160).ToArray();
    pacer.Enqueue(real);
    pacer.Enqueue(silence);
    pacer.Enqueue(silence);

    var sw = Stopwatch.StartNew();
    while (pacer.IsPlaying && sw.ElapsedMilliseconds < 3000)
        await Task.Delay(20);
    await Task.Delay(100); // allow SendingComplete
    Check("SendingComplete after real+silence tail", completeCount >= 1);
    Check("IsPlaying false with silence still possibly pending/sent", !pacer.IsPlaying);
    await pacer.Stop();
}

// --- ApplyFilter ducking should not prevent completion ---
{
    var pacer = new RtpPacedSender();
    int completeCount = 0;
    pacer.SendingComplete += () => completeCount++;
    pacer.SendAction = (_, _) => { };
    // Filter that turns everything into silence-looking bytes (the old bug path)
    pacer.ApplyFilter(frame => Enumerable.Repeat((byte)0x7F, frame.Length).ToArray());
    pacer.Start();

    var real = Enumerable.Repeat((byte)0x33, 160).ToArray();
    pacer.Enqueue(real);
    pacer.Enqueue(real);

    Check("IsPlaying true even when filter will duck to silence", pacer.IsPlaying);
    var sw = Stopwatch.StartNew();
    while (pacer.IsPlaying && sw.ElapsedMilliseconds < 3000)
        await Task.Delay(20);
    await Task.Delay(50);
    Check("SendingComplete fires despite ducking filter", completeCount >= 1);
    await pacer.Stop();
}

Console.WriteLine(failures == 0 ? "\nAll pacer smoke tests passed." : $"\n{failures} failure(s).");
Environment.Exit(failures == 0 ? 0 : 1);
