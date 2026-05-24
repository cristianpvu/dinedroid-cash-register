using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FiscalAgent.Fiscal;

/// <summary>
/// Talks to the local FiscalNet REST server (POST {BaseUrl}/api/Receipt) with a JSON
/// array of command strings, then parses the BONOK/NRBON/ERRCODE response.
/// </summary>
public sealed class FiscalNetDevice : IFiscalDevice
{
    private readonly HttpClient _http;
    private readonly ILogger<FiscalNetDevice> _log;

    public FiscalNetDevice(HttpClient http, ILogger<FiscalNetDevice> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<FiscalResult> PrintReceiptAsync(ReceiptJob job, CancellationToken ct)
    {
        var commands = FiscalNetCommandBuilder.Build(job);
        var body = JsonSerializer.Serialize(commands);
        _log.LogInformation("FiscalNet POST /api/Receipt: {Body}", body);

        HttpResponseMessage resp;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            resp = await _http.PostAsync("/api/Receipt", content, ct);
        }
        catch (Exception ex)
        {
            // Connection error/timeout: the request may have reached FiscalNet and printed.
            // We do NOT know — mark uncertain so it's never blindly retried.
            _log.LogError(ex, "FiscalNet request failed");
            return FiscalResult.Unknown("CONN_ERROR", ex.Message);
        }

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return FiscalResult.Unknown($"HTTP_{(int)resp.StatusCode}", $"FiscalNet HTTP {(int)resp.StatusCode}", raw);

        return ParseResponse(raw);
    }

    private FiscalResult ParseResponse(string raw)
    {
        var kv = ExtractKeyValues(raw);

        // Success/failure signal: HTTP API uses ReceiptStatus(bool); file mode uses BONOK(1/0/-1).
        bool? success = null;
        var posUnconfirmed = false;

        if (kv.TryGetValue("ReceiptStatus", out var rs))
        {
            var v = rs.Trim();
            success = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
        else if (kv.TryGetValue("BONOK", out var bonok))
        {
            var code = bonok.Trim();
            if (code == "1") success = true;
            else if (code == "-1") posUnconfirmed = true; // POS: amount sent, response not identified
            else success = false;
        }

        var bon = First(kv, "ReceiptNumber", "NRBON");
        var errCode = First(kv, "ErrorCode", "ERRCODE");
        var errInfo = First(kv, "ErrorInfo", "ERRINFO", "ReceiptInfo");

        if (success == true)
            return FiscalResult.Success(string.IsNullOrWhiteSpace(bon) ? null : bon!.Trim(), raw);

        if (success == false)
            // Device positively reported it did not print (e.g. not connected, paper out). Safe to retry.
            return FiscalResult.Failure(
                string.IsNullOrWhiteSpace(errCode) ? "DEVICE_ERROR" : errCode!.Trim(),
                string.IsNullOrWhiteSpace(errInfo) ? "FiscalNet reported failure" : errInfo!.Trim(),
                raw);

        if (posUnconfirmed)
            return FiscalResult.Unknown("POS_UNCONFIRMED",
                string.IsNullOrWhiteSpace(errInfo) ? "Bank terminal response not identified" : errInfo!.Trim(), raw);

        // No recognized status field at all — we genuinely cannot tell. Never assume printed.
        _log.LogWarning("FiscalNet response without a recognized status field: {Raw}", raw);
        return FiscalResult.Unknown("UNKNOWN_RESPONSE", "FiscalNet response had no status field", raw);
    }

    private static string? First(Dictionary<string, string> kv, params string[] keys)
    {
        foreach (var k in keys)
            if (kv.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    /// <summary>
    /// FiscalNet's HTTP response shape isn't fixed in the docs (the file mode returns
    /// KEY=VALUE lines). Parse defensively: JSON object, JSON array/string, or plain text.
    /// </summary>
    private static Dictionary<string, string> ExtractKeyValues(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        raw = raw?.Trim() ?? "";
        if (raw.Length == 0) return dict;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    var val = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? ""
                        : p.Value.ToString();
                    dict[p.Name] = val;
                }
                if (!dict.ContainsKey("BONOK") && !dict.ContainsKey("ReceiptStatus"))
                    ScanText(dict, string.Join("\n", dict.Values));
                return dict;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var el in root.EnumerateArray())
                    sb.AppendLine(el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString());
                ScanText(dict, sb.ToString());
                return dict;
            }

            if (root.ValueKind == JsonValueKind.String)
            {
                ScanText(dict, root.GetString() ?? "");
                return dict;
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through to plain text
        }

        ScanText(dict, raw);
        return dict;
    }

    private static void ScanText(Dictionary<string, string> dict, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            if (key.Length == 0) continue;
            dict[key] = line[(idx + 1)..].Trim();
        }
    }
}
