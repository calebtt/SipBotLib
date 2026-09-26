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

    /// <summary>
    /// Loads SIP settings from a JSON file (if present) then overlays environment variables.
    /// Environment variables win when both are set.
    /// </summary>
    /// <remarks>
    /// Precedence: <c>SIP_*</c> env vars overlay file values. File path is
    /// <paramref name="filePath"/>, else <c>SIP_SETTINGS_PATH</c>, else <c>sipsettings.json</c>
    /// in the current directory if it exists. Env-only is allowed when
    /// <c>SIP_SERVER</c> and <c>SIP_USERNAME</c> are set (password may be empty for
    /// open lab PBXs, but is required for typical auth).
    /// </remarks>
    public static SipConfig LoadActiveConfig(string? filePath = null, int configIndex = 0)
    {
        string? path = ResolveSettingsPath(filePath);
        if (path != null)
            LoadSettingsFromJson(path);

        SipConfig cfg;
        if (_settings != null && _settings.Configs.Count > 0)
        {
            if (configIndex < 0 || configIndex >= _settings.Configs.Count)
                throw new InvalidOperationException($"config index {configIndex} out of range (0..{_settings.Configs.Count - 1})");
            cfg = _settings.Configs[configIndex];
        }
        else
        {
            cfg = new SipConfig();
            Settings = new SipSettingsConfig { Configs = { cfg } };
        }

        ApplyEnvironmentOverrides(cfg);

        if (cfg.Port <= 0)
            cfg.Port = 5060;
        if (string.IsNullOrWhiteSpace(cfg.FromName))
            cfg.FromName = cfg.Username;

        if (string.IsNullOrWhiteSpace(cfg.Server) || string.IsNullOrWhiteSpace(cfg.Username))
        {
            throw new InvalidOperationException(
                "SIP config incomplete. Set SIP_SERVER and SIP_USERNAME (and usually SIP_PASSWORD), " +
                "or provide sipsettings.json / SIP_SETTINGS_PATH.");
        }

        return cfg;
    }

    /// <summary>
    /// Overlay <c>SIP_*</c> environment variables onto a config. Env wins over file values.
    /// </summary>
    public static void ApplyEnvironmentOverrides(SipConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Overlay(config, "SIP_SERVER", v => config.Server = v);
        Overlay(config, "SIP_USERNAME", v => config.Username = v);
        Overlay(config, "SIP_PASSWORD", v => config.Password = v);
        Overlay(config, "SIP_FROMNAME", v => config.FromName = v);

        string? port = Environment.GetEnvironmentVariable("SIP_PORT");
        if (!string.IsNullOrWhiteSpace(port) && int.TryParse(port, out var p) && p > 0)
            config.Port = p;
    }

    /// <summary>
    /// Local UDP bind port. <c>SIP_LOCAL_PORT</c> if set and &gt;= 0; otherwise <paramref name="defaultPort"/>
    /// (0 = ephemeral). Do not bind 5060 when co-located with Asterisk.
    /// </summary>
    public static int GetLocalBindPort(int defaultPort = 0)
    {
        string? env = Environment.GetEnvironmentVariable("SIP_LOCAL_PORT");
        if (int.TryParse(env, out var p) && p >= 0)
            return p;
        return defaultPort;
    }

    public static string? ResolveSettingsPath(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
            return filePath;

        string? fromEnv = Environment.GetEnvironmentVariable("SIP_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        if (File.Exists("sipsettings.json"))
            return "sipsettings.json";

        string nextToApp = Path.Combine(AppContext.BaseDirectory, "sipsettings.json");
        if (File.Exists(nextToApp))
            return nextToApp;

        return null;
    }

    internal static void ResetForTests() => _settings = null;

    private static void Overlay(SipConfig _, string name, Action<string> apply)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            apply(value);
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

    /// <summary>
    /// How <see cref="SipClient"/> retries registration after a failure. The defaults keep the
    /// library's original schedule.
    /// </summary>
    [JsonPropertyName("registrationRetry")]
    public RegistrationRetryOptions RegistrationRetry { get; set; } = new();
}

/// <summary>
/// Registration retry settings for <see cref="SipClient"/>.
/// </summary>
/// <remarks>
/// A temporary failure (timeout, 5xx, and any status that is not a hard failure) is retried.
/// A hard failure (401/407 after authentication, 402, 403, 404) is never retried automatically:
/// the client stops registering until <see cref="SipClient.StartRegistration"/> is called again.
/// </remarks>
public class RegistrationRetryOptions
{
    /// <summary>
    /// Extended retry, for always-on hosts: after a temporary failure the client retries forever,
    /// the delay doubling from <see cref="ExtendedInitialDelayMs"/> up to
    /// <see cref="ExtendedMaxDelayMs"/>. Off by default.
    /// </summary>
    [JsonPropertyName("extended")]
    public bool Extended { get; set; }

    [JsonPropertyName("extendedInitialDelayMs")]
    public int ExtendedInitialDelayMs { get; set; } = 30_000;

    [JsonPropertyName("extendedMaxDelayMs")]
    public int ExtendedMaxDelayMs { get; set; } = 300_000;

    /// <summary>
    /// Default retry: after a temporary failure the client makes up to this many attempts,
    /// attempt n after <see cref="DefaultBaseDelayMs"/> × n. After the last one the registration
    /// agent's own retry stays armed, so the process still recovers without a restart.
    /// </summary>
    [JsonPropertyName("defaultMaxAttempts")]
    public int DefaultMaxAttempts { get; set; } = 5;

    [JsonPropertyName("defaultBaseDelayMs")]
    public int DefaultBaseDelayMs { get; set; } = 2_000;

    // Timings below are passed through to SIPSorcery or drive the health check. They are not
    // part of the settings file; tests shorten them.

    /// <summary>Seconds between the registration agent's own retries after a failure (SIPSorcery default 300).</summary>
    [JsonIgnore]
    internal int AgentFailureRetrySeconds { get; set; } = 300;

    /// <summary>Seconds the registration agent waits for a REGISTER response (SIPSorcery default 60).</summary>
    [JsonIgnore]
    internal int AgentAttemptTimeoutSeconds { get; set; } = 60;

    [JsonIgnore]
    internal int HealthCheckIntervalMs { get; set; } = 30_000;

    /// <summary>How long registration must be missing before the health check acts.</summary>
    [JsonIgnore]
    internal int HealthCheckStaleMs { get; set; } = 120_000;
}