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
