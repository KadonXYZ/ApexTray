using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApexTray.Core.Protocol;

namespace ApexTray.Core.Firmware;

public sealed record FirmwareRelease(FirmwareVersion Version, string Notes, string FilePath);

// Asks the RongYuan update server the official AJAZZ app uses which firmware is published for a device.
public static partial class FirmwareUpdates
{
    private const string Endpoint = "https://api2.rongyuan.tech:3816/api/v2/get_fw_version";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Returns the published release, or null when the server has none for this device.
    public static async Task<FirmwareRelease?> GetLatestAsync(int deviceId, CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync(Endpoint, new { dev_id = deviceId }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.InternalServerError && body.Contains("Record not found", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The AJAZZ update server answered {(int)response.StatusCode}: {body}".Trim());
        return Parse(body);
    }

    // Response shape: {"code":0,"data":{"version_str":"v0302_rf...","description":"...","file_path":"..."}}.
    internal static FirmwareRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0) return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        // The first "_"-separated token is the main firmware, e.g. "v0302"; the rest are radio/screen parts.
        var versionStr = data.TryGetProperty("version_str", out var v) ? v.GetString() : null;
        var main = versionStr?.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (main is not { Length: > 1 } || char.ToLowerInvariant(main[0]) != 'v'
            || !ushort.TryParse(main[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return null;

        var notes = data.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var file = data.TryGetProperty("file_path", out var f) ? f.GetString() ?? "" : "";
        return new FirmwareRelease(new FirmwareVersion(raw), PlainText(notes), file);
    }

    // The vendor app renders the notes as HTML.
    private static string PlainText(string html)
    {
        var text = LineBreak().Replace(html, "\n");
        text = Tag().Replace(text, "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();
}
