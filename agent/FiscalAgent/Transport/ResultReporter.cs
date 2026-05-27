using System.Text;
using System.Text.Json;
using FiscalAgent.Configuration;
using FiscalAgent.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiscalAgent.Transport;

public sealed class ResultReporter
{
    public const string HttpClientName = "backend-api";

    private readonly IHttpClientFactory _factory;
    private readonly AgentOptions _opts;
    private readonly ILogger<ResultReporter> _log;

    public ResultReporter(IHttpClientFactory factory, IOptions<AgentOptions> opts, ILogger<ResultReporter> log)
    {
        _factory = factory;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Reports a print job result to the cloud.
    /// </summary>
    public async Task<bool> ReportAsync(ResultMessage result, CancellationToken ct)
    {
        var path = _opts.Backend.ResultPath.Replace("{jobId}", Uri.EscapeDataString(result.JobId));
        return await PostAsync(path, result, result.JobId, ct);
    }

    /// <summary>
    /// Reports a fiscal command result (X/Z report, cash, etc.) to the cloud.
    /// </summary>
    public async Task<bool> ReportCommandAsync(CommandResultMessage result, CancellationToken ct)
    {
        var path = _opts.Backend.CommandResultPath.Replace("{commandId}", Uri.EscapeDataString(result.CommandId));
        return await PostAsync(path, result, result.CommandId, ct);
    }

    private async Task<bool> PostAsync(string path, object payload, string id, CancellationToken ct)
    {
        try
        {
            var client = _factory.CreateClient(HttpClientName);
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(path, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Report for {Id} returned HTTP {Code}", id, (int)resp.StatusCode);
                return false;
            }
            _log.LogInformation("Reported {Id} successfully", id);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to report result for {Id}", id);
            return false;
        }
    }
}
