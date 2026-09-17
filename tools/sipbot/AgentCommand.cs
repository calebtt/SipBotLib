using System.Text.Json;
using System.Text.Json.Serialization;

namespace SipBot.Cli;

/// <summary>One stdin JSON command object. Unknown fields are ignored.</summary>
public sealed class AgentCommand
{
    [JsonPropertyName("cmd")]
    public string? Cmd { get; set; }

    [JsonPropertyName("play")]
    public string? Play { get; set; }

    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("seconds")]
    public int? Seconds { get; set; }

    [JsonPropertyName("timeoutSec")]
    public int? TimeoutSec { get; set; }

    [JsonPropertyName("maxDigits")]
    public int? MaxDigits { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    public static AgentCommand Parse(string line)
    {
        var cmd = JsonSerializer.Deserialize<AgentCommand>(line, JsonOptions)
            ?? throw new InvalidOperationException("Command JSON deserialized to null.");
        if (string.IsNullOrWhiteSpace(cmd.Cmd))
            throw new InvalidOperationException("Command JSON missing 'cmd'.");
        cmd.Cmd = cmd.Cmd.Trim().ToLowerInvariant();
        return cmd;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
