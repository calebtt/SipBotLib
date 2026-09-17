using SipBot;
using SIPSorceryMedia.Abstractions;

namespace SipBot.Cli;

/// <summary>Advertised vs negotiated audio, for JSONL events and play errors.</summary>
public static class AudioCaps
{
    public const string PlayHelp =
        "Play WAV is resampled to the negotiated codec (PCMU 8 kHz default; G.722 16 kHz unless --pcmu-only).";

    public const string PlayHint =
        "any PCM/μ-law WAV; source sample rate does not matter (resampled to the negotiated codec: PCMU 8 kHz default, G.722 16 kHz unless --pcmu-only)";

    public static Dictionary<string, object?> Advertised(bool pcmuOnly) => new()
    {
        ["codecs"] = pcmuOnly ? new[] { "PCMU" } : new[] { "PCMU", "G722" },
        ["playHint"] = PlayHint,
        ["pcmuOnly"] = pcmuOnly
    };

    public static Dictionary<string, object?> Negotiated(AudioFormat format) => new()
    {
        ["codec"] = format.FormatName,
        ["clockRateHz"] = format.ClockRate,
        ["rtpClockRateHz"] = format.RtpClockRate
    };

    public static Dictionary<string, object?> ForStatus(bool pcmuOnly, bool callActive, AudioFormat? negotiated)
    {
        var audio = Advertised(pcmuOnly);
        if (callActive && negotiated is { } fmt)
            audio["negotiated"] = Negotiated(fmt);
        return audio;
    }

    public static string UnreadableWavMessage(string path, string? detail = null)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? "" : $" {detail}";
        return $"'{path}' is not a readable WAV (need PCM or μ-law). {PlayHelp}{suffix}";
    }
}
