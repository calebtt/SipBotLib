using SipBot.Cli;
using Xunit;

namespace SipBotLib.Tests;

public class AgentCommandTests
{
    [Fact]
    public void Parse_DialCommand()
    {
        var cmd = AgentCommand.Parse("""{"cmd":"dial","uri":"102","play":"hello.wav"}""");
        Assert.Equal("dial", cmd.Cmd);
        Assert.Equal("102", cmd.Uri);
        Assert.Equal("hello.wav", cmd.Play);
    }

    [Fact]
    public void Parse_WaitDtmf()
    {
        var cmd = AgentCommand.Parse("""{"cmd":"wait_dtmf","timeoutSec":30,"maxDigits":4}""");
        Assert.Equal("wait_dtmf", cmd.Cmd);
        Assert.Equal(30, cmd.TimeoutSec);
        Assert.Equal(4, cmd.MaxDigits);
    }

    [Fact]
    public void Parse_MissingCmd_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AgentCommand.Parse("""{"uri":"102"}"""));
    }
}

public class AudioCapsTests
{
    [Fact]
    public void Advertised_IncludesG722UnlessPcmuOnly()
    {
        var wide = AudioCaps.Advertised(pcmuOnly: false);
        Assert.Equal(new[] { "PCMU", "G722" }, Assert.IsType<string[]>(wide["codecs"]));
        Assert.Equal(false, wide["pcmuOnly"]);
        Assert.Contains("resampled", Assert.IsType<string>(wide["playHint"]), StringComparison.OrdinalIgnoreCase);

        var narrow = AudioCaps.Advertised(pcmuOnly: true);
        Assert.Equal(new[] { "PCMU" }, Assert.IsType<string[]>(narrow["codecs"]));
        Assert.Equal(true, narrow["pcmuOnly"]);
    }

    [Fact]
    public void ForStatus_AddsNegotiatedOnlyWhenCallActive()
    {
        var idle = AudioCaps.ForStatus(pcmuOnly: false, callActive: false, negotiated: null);
        Assert.False(idle.ContainsKey("negotiated"));

        var fmt = new SIPSorceryMedia.Abstractions.AudioFormat(SIPSorceryMedia.Abstractions.SDPWellKnownMediaFormatsEnum.G722);
        var live = AudioCaps.ForStatus(pcmuOnly: false, callActive: true, negotiated: fmt);
        var negotiated = Assert.IsType<Dictionary<string, object?>>(live["negotiated"]);
        Assert.Equal("G722", negotiated["codec"]);
        Assert.Equal(16000, negotiated["clockRateHz"]);
    }

    [Fact]
    public void UnreadableWavMessage_SaysRateDoesNotMatter()
    {
        string msg = AudioCaps.UnreadableWavMessage("prompt.mp3");
        Assert.Contains("not a readable WAV", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resampled", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PCMU", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayWav_RejectsNonWavWithResampleHint()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sipbot-play-{Guid.NewGuid():N}.mp3");
        File.WriteAllText(path, "not a wav");
        try
        {
            var ep = new AgentAudioEndPoint(keepAlive: false, wideband: false);
            var ex = Assert.Throws<InvalidOperationException>(() => ep.PlayWav(path));
            Assert.Contains("not a readable WAV", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("resampled", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
