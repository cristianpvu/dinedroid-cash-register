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
    /// Reports a result to the cloud. If this fails, correctness is preserved: the job state
    /// is already persisted locally, so the backend can redeliver and we re-report from store.
    /// </summary>
    public async Task<bool> ReportAsync(ResultMessage result, CancellationToken ct)
    {
        var path = _opts.Backend.ResultPath.Replace("{jobId}", Uri.EscapeDataString(result.JobId));
        try
        {
            var client = _factory.CreateClient(HttpClientName);
            var json = JsonSerializer.Serialize(result);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(path, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Result report for job {Id} returned HTTP {Code}", result.JobId, (int)resp.StatusCode);
                return false;
            }
            _log.LogInformation("Reported job {Id} as {Status}", result.JobId, result.Status);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to report result for job {Id}", result.JobId);
            return false;
        }
    }
}
