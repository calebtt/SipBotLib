using System.Text.Json;
using System.Text.Json.Serialization;
namespace SipBot;

public static class SipBotSettings
{
    private static SipSettingsConfig? _settings;

    public static SipSettingsConfig Settings
    {
        get => _settings ?? throw new InvalidOperationException("SIP settings not loaded. Call LoadSettingsFromJson first.");
        private set => _settings = value;
    }

    public static void LoadSettingsFromJson(string filePath = "sipsettings.json")
    {
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            string currentDir = Directory.GetCurrentDirectory();
            throw new FileNotFoundException($"File not found at: {filePath} - resolved to {fullPath}\nCurrent Working Dir: {currentDir}");
        }

        string jsonString = File.ReadAllText(fullPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var settings = JsonSerializer.Deserialize<SipSettingsWrapper>(jsonString, options);

        Settings = settings?.SipSettings ?? throw new InvalidOperationException("Failed to deserialize SIP settings from JSON.");
    }
}

public class SipSettingsWrapper
{
    [JsonPropertyName("SipSettings")]
    public SipSettingsConfig SipSettings { get; set; } = new SipSettingsConfig();
}

public class SipSettingsConfig
{
    [JsonPropertyName("Configs")]
    public List<SipConfig> Configs { get; set; } = new List<SipConfig>();
}

public class SipConfig
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("fromname")]
    public string FromName { get; set; } = string.Empty;
}