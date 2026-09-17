namespace SipBot;

/// <summary>
/// Normalizes transfer/dial targets to a parseable SIP URI.
/// Accepts a full <c>sip:</c> URI, <c>user@host</c>, or a bare extension.
/// </summary>
public static class SipUriNormalizer
{
    /// <summary>
    /// Accepts <c>sip:102@host</c>, <c>102@host</c>, or bare <c>102</c>.
    /// Bare extensions become <c>sip:{extension}@{defaultServer}</c>.
    /// </summary>
    public static string Normalize(string target, string defaultServer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string t = target.Trim();
        if (t.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            return t;
        if (t.Contains('@', StringComparison.Ordinal))
            return "sip:" + t;

        string host = string.IsNullOrWhiteSpace(defaultServer) ? "localhost" : defaultServer.Trim();
        if (host.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        return $"sip:{t}@{host}";
    }

    /// <summary>Maps RFC 4733 telephone-event codes to a single DTMF character.</summary>
    public static char DtmfToneToChar(byte tone) => tone switch
    {
        10 => '*',
        11 => '#',
        12 => 'A',
        13 => 'B',
        14 => 'C',
        15 => 'D',
        _ when tone <= 9 => (char)('0' + tone),
        _ => '?'
    };
}
