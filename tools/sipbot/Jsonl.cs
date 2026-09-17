using System.Text.Json;

namespace SipBot.Cli;

/// <summary>Stdout is JSON Lines events only. Never write logs here.</summary>
static class Jsonl
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Emit(Dictionary<string, object?> fields)
    {
        string line = JsonSerializer.Serialize(fields, Options);
        lock (Gate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    public static void Event(string name, Dictionary<string, object?>? extra = null)
    {
        var fields = new Dictionary<string, object?> { ["event"] = name };
        if (extra != null)
        {
            foreach (var kv in extra)
                fields[kv.Key] = kv.Value;
        }
        Emit(fields);
    }

    public static void Error(string message) =>
        Event("error", new() { ["message"] = message });
}
