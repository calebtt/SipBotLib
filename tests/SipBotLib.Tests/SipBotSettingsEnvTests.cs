using SipBot;
using Xunit;

namespace SipBotLib.Tests;

public class SipBotSettingsEnvTests : IDisposable
{
    private readonly Dictionary<string, string?> _restore = new();
    private static readonly string[] Keys =
    [
        "SIP_SERVER", "SIP_PORT", "SIP_USERNAME", "SIP_PASSWORD",
        "SIP_FROMNAME", "SIP_SETTINGS_PATH", "SIP_LOCAL_PORT"
    ];

    public SipBotSettingsEnvTests()
    {
        foreach (var key in Keys)
        {
            _restore[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
        SipBotSettings.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var kv in _restore)
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        SipBotSettings.ResetForTests();
    }

    [Fact]
    public void ApplyEnvironmentOverrides_EnvWinsOverFile()
    {
        var cfg = new SipConfig
        {
            Server = "file.example.com",
            Port = 5060,
            Username = "fileuser",
            Password = "filepw",
            FromName = "File"
        };

        Environment.SetEnvironmentVariable("SIP_SERVER", "env.example.com");
        Environment.SetEnvironmentVariable("SIP_PORT", "5070");
        Environment.SetEnvironmentVariable("SIP_USERNAME", "envuser");
        Environment.SetEnvironmentVariable("SIP_PASSWORD", "envpw");
        Environment.SetEnvironmentVariable("SIP_FROMNAME", "Env");

        SipBotSettings.ApplyEnvironmentOverrides(cfg);

        Assert.Equal("env.example.com", cfg.Server);
        Assert.Equal(5070, cfg.Port);
        Assert.Equal("envuser", cfg.Username);
        Assert.Equal("envpw", cfg.Password);
        Assert.Equal("Env", cfg.FromName);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_LeavesFileValuesWhenEnvUnset()
    {
        var cfg = new SipConfig
        {
            Server = "file.example.com",
            Port = 5060,
            Username = "fileuser",
            Password = "filepw",
            FromName = "File"
        };

        SipBotSettings.ApplyEnvironmentOverrides(cfg);

        Assert.Equal("file.example.com", cfg.Server);
        Assert.Equal(5060, cfg.Port);
        Assert.Equal("fileuser", cfg.Username);
    }

    [Fact]
    public void GetLocalBindPort_ReadsEnv()
    {
        Assert.Equal(0, SipBotSettings.GetLocalBindPort());
        Environment.SetEnvironmentVariable("SIP_LOCAL_PORT", "5080");
        Assert.Equal(5080, SipBotSettings.GetLocalBindPort(0));
    }

    [Fact]
    public void LoadActiveConfig_EnvOnly()
    {
        Environment.SetEnvironmentVariable("SIP_SERVER", "pbx.example.com");
        Environment.SetEnvironmentVariable("SIP_USERNAME", "101");
        Environment.SetEnvironmentVariable("SIP_PASSWORD", "secret");

        var cfg = SipBotSettings.LoadActiveConfig(filePath: null);
        Assert.Equal("pbx.example.com", cfg.Server);
        Assert.Equal("101", cfg.Username);
        Assert.Equal("secret", cfg.Password);
        Assert.Equal(5060, cfg.Port);
        Assert.Equal("101", cfg.FromName);
    }
}
