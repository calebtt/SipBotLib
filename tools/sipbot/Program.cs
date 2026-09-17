using System.CommandLine;
using Serilog;
using Serilog.Events;
using SipBot.Cli;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        standardErrorFromLevel: LogEventLevel.Verbose,
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var settingsOpt = new Option<string?>("--settings")
{
    Description = "Path to sipsettings.json (else SIP_SETTINGS_PATH or sipsettings.json)"
};
var configOpt = new Option<int>("--config")
{
    Description = "Index into SipSettings.configs (default 0)",
    DefaultValueFactory = _ => 0
};
var autoAnswerOpt = new Option<bool>("--auto-answer")
{
    Description = "Accept and answer inbound INVITEs without an answer command"
};
var playOpt = new Option<string?>("--play")
{
    Description = "WAV to play after --auto-answer"
};
var pcmuOnlyOpt = new Option<bool>("--pcmu-only")
{
    Description = "Do not advertise G.722; PCMU/8 kHz only"
};
var noKeepAliveOpt = new Option<bool>("--no-keepalive")
{
    Description = "Disable continuous outbound RTP keep-alive (NAT pinholes may fail)"
};

var serve = new Command("serve", "Register with the PBX and run the JSONL control daemon")
{
    settingsOpt, configOpt, autoAnswerOpt, playOpt, pcmuOnlyOpt, noKeepAliveOpt
};

serve.SetAction(async (parseResult, ct) =>
{
    var options = new ServeOptions
    {
        SettingsPath = parseResult.GetValue(settingsOpt),
        ConfigIndex = parseResult.GetValue(configOpt),
        AutoAnswer = parseResult.GetValue(autoAnswerOpt),
        AutoAnswerPlay = parseResult.GetValue(playOpt),
        PcmuOnly = parseResult.GetValue(pcmuOnlyOpt),
        NoKeepAlive = parseResult.GetValue(noKeepAliveOpt)
    };

    using var cts = Shutdown.CreateCts();
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct);
    using var host = new ServeHost(options);
    try
    {
        await host.RunAsync(linked).ConfigureAwait(false);
        return 0;
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "sipbot serve failed");
        Jsonl.Error(ex.Message);
        return 1;
    }
    finally
    {
        Log.Information("sipbot shutting down");
        await Log.CloseAndFlushAsync().ConfigureAwait(false);
    }
});

var root = new RootCommand("sipbot — headless SIP control for coding agents (JSONL on stdout, logs on stderr)")
{
    serve
};

try
{
    return await root.Parse(args).InvokeAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
