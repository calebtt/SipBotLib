namespace SipBot;

/// <summary>
/// Normalizes transfer/dial targets to a parseable SIP URI.
/// Accepts a full <c>sip:</c> URI, <c>tel:</c> URI, <c>user@host</c>, a bare extension,
/// or a PSTN number (E.164 with <c>+</c> and common punctuation).
/// </summary>
public static class SipUriNormalizer
{
    internal const int MaxTargetLength = 1024;

    /// <summary>
    /// Accepts <c>sip:102@host</c>, <c>102@host</c>, bare <c>102</c>, <c>tel:+1…</c>,
    /// or a phone number. Bare extensions become <c>sip:{extension}@{defaultServer}</c>.
    /// PSTN user parts are reduced to ASCII digits (no leading <c>+</c>) so PBX outbound
    /// routes that match NANP without <c>+</c> still work.
    /// A <c>tel:</c> URI is always a number on <paramref name="defaultServer"/>:
    /// <c>;</c> parameters and a trailing <c>@host</c> are not a SIP target.
    /// </summary>
    public static string Normalize(string target, string defaultServer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        RejectUnsafe(target, nameof(target));
        if (!string.IsNullOrEmpty(defaultServer))
            RejectUnsafe(defaultServer, nameof(defaultServer));

        string t = target.Trim();
        string host = string.IsNullOrWhiteSpace(defaultServer) ? "localhost" : defaultServer.Trim();
        if (host.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            host = host[4..];

        if (t.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return NormalizeTel(t, host);

        if (t.StartsWith("//", StringComparison.Ordinal))
            t = t[2..].Trim();

        if (t.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = t[4..];
            int at = rest.IndexOf('@');
            if (at <= 0)
                return t;
            return "sip:" + PstnUserDigits(rest[..at]) + rest[at..];
        }

        if (t.Contains('@', StringComparison.Ordinal))
        {
            int at = t.IndexOf('@');
            return "sip:" + PstnUserDigits(t[..at]) + t[at..];
        }

        return $"sip:{PstnUserDigits(t)}@{host}";
    }

    /// <summary>
    /// <c>tel:</c> subscriber only. Parameters (<c>;phone-context</c>, <c>;ext</c>) and a
    /// spurious <c>@host</c> are dropped. The number is dialed on <paramref name="host"/>.
    /// </summary>
    private static string NormalizeTel(string t, string host)
    {
        string subscriber = t[4..].Trim();
        if (subscriber.StartsWith("//", StringComparison.Ordinal))
            subscriber = subscriber[2..].Trim();

        int cut = subscriber.Length;
        foreach (char sep in ";@?")
        {
            int i = subscriber.IndexOf(sep);
            if (i >= 0 && i < cut)
                cut = i;
        }
        if (cut < subscriber.Length)
            subscriber = subscriber[..cut];
        subscriber = subscriber.Trim();

        if (subscriber.Length == 0 || !IsTelSubscriber(subscriber))
            throw new ArgumentException("tel: URI is not a phone number.", "target");

        return $"sip:{PstnUserDigits(subscriber)}@{host}";
    }

    /// <summary>Digits, visual separators, and <c>*#</c>. Not a SIP user or scheme.</summary>
    private static bool IsTelSubscriber(string subscriber)
    {
        foreach (char c in subscriber)
        {
            if (char.IsAsciiDigit(c) || c is '+' or '*' or '#' or ' ' or '-' or '(' or ')' or '.' or '/')
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// If <paramref name="user"/> looks like a phone number, return ASCII digits only;
    /// otherwise return it unchanged (extensions, SIP usernames).
    /// </summary>
    public static string PstnUserDigits(string user)
    {
        if (!LooksLikePstn(user))
            return user;
        var digits = new string(user.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 ? digits : user;
    }

    /// <summary>
    /// True for E.164 / NANP-shaped values (<c>+1…</c>, punctuation, or 10–15 ASCII digits).
    /// Short digit strings such as <c>102</c> stay extensions. Unicode digits do not count.
    /// </summary>
    public static bool LooksLikePstn(string user)
    {
        string s = user.Trim();
        if (s.Length == 0)
            return false;
        int n = 0;
        bool plus = false;
        bool punct = false;
        foreach (char c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                n++;
                continue;
            }
            if (c == '+')
            {
                plus = true;
                continue;
            }
            if (c is ' ' or '-' or '(' or ')' or '.' or '/')
            {
                punct = true;
                continue;
            }
            return false;
        }
        if (n is < 8 or > 15)
            return false;
        return plus || punct || n >= 10;
    }

    /// <summary>
    /// Rejects oversized targets and C0/DEL controls (including CR/LF/NUL), plus
    /// percent-encoded NUL/CR/LF. Those must not land in a SIP request line.
    /// </summary>
    internal static void RejectUnsafe(string value, string paramName)
    {
        if (value.Length > MaxTargetLength)
            throw new ArgumentException("SIP target exceeds maximum length.", paramName);

        foreach (char c in value)
        {
            if (char.IsControl(c))
                throw new ArgumentException("SIP target contains a control character.", paramName);
        }

        if (value.Contains("%00", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("%0d", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("%0a", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SIP target contains a percent-encoded control character.", paramName);
        }
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
