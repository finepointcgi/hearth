using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Locus.Core.Services.Phone;

/// <summary>
/// The small part of Twilio that phone calls need: checking that requests really come from Twilio, the TwiML that
/// hands a call to ConversationRelay (Twilio does speech-to-text and text-to-speech; Locus gets and sends text), and
/// the REST calls to place a call and check the account.
/// </summary>
public static class Twilio
{
    public const string ApiBase = "https://api.twilio.com/2010-04-01";

    /// <summary>
    /// X-Twilio-Signature: base64(HMAC-SHA1(authToken, url + each POST parameter's name and value, sorted by name)).
    /// </summary>
    public static string Sign(string authToken, string url, IEnumerable<KeyValuePair<string, string>>? form = null)
    {
        var data = new StringBuilder(url);
        foreach (var (k, v) in (form ?? []).OrderBy(p => p.Key, StringComparer.Ordinal)) data.Append(k).Append(v);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data.ToString())));
    }

    /// <summary>
    /// Whether <paramref name="signature"/> is Twilio's signature for this request. The URL is the public one Twilio
    /// called (not the local address behind a tunnel). For WebSocket handshakes Twilio's docs don't say whether the
    /// signed URL starts wss:// or https://, or ends with a slash, so each form is accepted.
    /// </summary>
    public static bool IsValid(string authToken, string url, IEnumerable<KeyValuePair<string, string>>? form, string? signature)
    {
        if (string.IsNullOrEmpty(authToken) || string.IsNullOrEmpty(signature)) return false;
        var pairs = form?.ToList() ?? [];
        var forms = new List<string> { url };
        if (url.StartsWith("wss://")) forms.Add("https://" + url[6..]);
        if (url.StartsWith("https://") && pairs.Count == 0) forms.Add("wss://" + url[8..]);
        foreach (var u in forms.ToList()) forms.Add(u.Contains('?') ? u : u.TrimEnd('/') + "/");
        var given = Encoding.ASCII.GetBytes(signature);
        return forms.Distinct().Any(u => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(authToken, u, pairs)), given));
    }

    private static string Attr(string value) => SecurityElement.Escape(value) ?? "";

    /// <summary>TwiML that connects the call to Locus over ConversationRelay.</summary>
    public static string RelayTwiml(string relayUrl, string actionUrl, PhoneConfig config, IReadOnlyDictionary<string, string> parameters,
        string? greeting = null)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response>")
            .Append($"<Connect action=\"{Attr(actionUrl)}\">")
            .Append($"<ConversationRelay url=\"{Attr(relayUrl)}\" language=\"{Attr(config.Language)}\" interruptible=\"any\" dtmfDetection=\"true\"");
        if (!string.IsNullOrWhiteSpace(config.TtsProvider)) sb.Append($" ttsProvider=\"{Attr(config.TtsProvider)}\"");
        if (!string.IsNullOrWhiteSpace(config.Voice)) sb.Append($" voice=\"{Attr(config.Voice)}\"");
        if (!string.IsNullOrWhiteSpace(greeting)) sb.Append($" welcomeGreeting=\"{Attr(greeting)}\"");
        sb.Append('>');
        foreach (var (k, v) in parameters) sb.Append($"<Parameter name=\"{Attr(k)}\" value=\"{Attr(v)}\"/>");
        return sb.Append("</ConversationRelay></Connect></Response>").ToString();
    }

    /// <summary>TwiML that says something and hangs up.</summary>
    public static string SayAndHangUp(string text) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Say>{Attr(text)}</Say><Hangup/></Response>";

    public const string Empty = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response/>";

    private static HttpRequestMessage Request(HttpMethod method, string url, string accountSid, string authToken)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{accountSid}:{authToken}")));
        return req;
    }

    private static string Error(string body)
    {
        try { return JsonNode.Parse(body)?["message"]?.ToString() ?? body; }
        catch { return body; }
    }

    /// <summary>Places a call that runs <paramref name="twiml"/> when answered. Returns the call's sid.</summary>
    public static async Task<string> CreateCallAsync(HttpClient http, string accountSid, string authToken, string to, string from,
        string twiml, string statusCallback, CancellationToken ct, string apiBase = ApiBase)
    {
        using var req = Request(HttpMethod.Post, $"{apiBase}/Accounts/{accountSid}/Calls.json", accountSid, authToken);
        req.Content = new FormUrlEncodedContent(
        [
            new("To", to), new("From", from), new("Twiml", twiml), new("Timeout", "30"),
            new("StatusCallback", statusCallback),
            new("StatusCallbackEvent", "answered"), new("StatusCallbackEvent", "completed"),
        ]);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Twilio couldn't place the call: {Error(body)}");
        return JsonNode.Parse(body)?["sid"]?.ToString() ?? "";
    }

    /// <summary>Checks the account sid and auth token; returns the account's name.</summary>
    public static async Task<string> CheckAccountAsync(HttpClient http, string accountSid, string authToken, CancellationToken ct,
        string apiBase = ApiBase)
    {
        using var req = Request(HttpMethod.Get, $"{apiBase}/Accounts/{accountSid}.json", accountSid, authToken);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Twilio: {Error(body)}");
        return JsonNode.Parse(body)?["friendly_name"]?.ToString() ?? accountSid;
    }

    /// <summary>"(313) 555-0100", "313.555.0100" → "+13135550100" (US default); null when it isn't a phone number.</summary>
    public static string? Normalize(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var plus = number.TrimStart().StartsWith('+');
        var digits = new string(number.Where(char.IsDigit).ToArray());
        if (plus) return digits.Length is >= 8 and <= 15 ? "+" + digits : null;
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits[0] == '1') return "+" + digits;
        return null;
    }

    /// <summary>Numbers bots must never call: emergency services and premium-rate lines.</summary>
    public static string? Forbidden(string e164)
    {
        var digits = e164.TrimStart('+');
        if (digits is "911" or "112" or "999" or "988" or "000" || digits.Length <= 6) return "Bots can't call emergency or short numbers.";
        if (digits.StartsWith("1900") || digits.StartsWith("1976")) return "Bots can't call premium-rate numbers.";
        return null;
    }
}
