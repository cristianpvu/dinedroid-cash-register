using System.Text;
using System.Text.Json;
using FiscalAgent.Configuration;
using FiscalAgent.Contracts;
using FiscalAgent.Jobs;
using FiscalAgent.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiscalAgent.Transport;

/// <summary>
/// Holds a long-lived Server-Sent-Events connection to the cloud, receives fiscal jobs,
/// runs them through the <see cref="JobProcessor"/>, reports results, and resumes from the
/// last processed event id (Last-Event-ID) after any disconnect.
/// </summary>
public sealed class SseClient : BackgroundService
{
    public const string HttpClientName = "backend-sse";
    private const string CursorKey = "sse_last_event_id";

    private readonly IHttpClientFactory _factory;
    private readonly JobProcessor _processor;
    private readonly JobStore _store;
    private readonly ResultReporter _reporter;
    private readonly AgentOptions _opts;
    private readonly ILogger<SseClient> _log;

    public SseClient(
        IHttpClientFactory factory,
        JobProcessor processor,
        JobStore store,
        ResultReporter reporter,
        IOptions<AgentOptions> opts,
        ILogger<SseClient> log)
    {
        _factory = factory;
        _processor = processor;
        _store = store;
        _reporter = reporter;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _store.InitAsync(ct);

        if (!_opts.Backend.Enabled)
        {
            _log.LogInformation("Backend disabled — agent running in offline/test mode (no SSE connection).");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SSE connection dropped; reconnecting in {Sec}s", _opts.Backend.ReconnectDelaySeconds);
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(_opts.Backend.ReconnectDelaySeconds), ct);
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        var client = _factory.CreateClient(HttpClientName);

        using var req = new HttpRequestMessage(HttpMethod.Get, _opts.Backend.StreamPath);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        var lastId = await _store.GetCursorAsync(CursorKey, ct);
        if (!string.IsNullOrEmpty(lastId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastId);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        _log.LogInformation("SSE connected ({Path}), resuming from id={LastId}",
            _opts.Backend.StreamPath, lastId ?? "<start>");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventId = null;
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // server closed the stream

            if (line.Length == 0)
            {
                if (data.Length > 0)
                    await HandleEventAsync(eventId, data.ToString(), ct);
                eventId = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':') continue; // comment / heartbeat ping

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                eventId = line[3..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' ')) value = value[1..]; // SSE: strip one leading space
                data.Append(value).Append('\n');
            }
            // "event:" and "retry:" intentionally ignored — single event type.
        }
    }

    private async Task HandleEventAsync(string? eventId, string data, CancellationToken ct)
    {
        JobMessage? job = null;
        try
        {
            job = JsonSerializer.Deserialize<JobMessage>(data.Trim());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Discarding unparseable job payload: {Data}", data);
        }

        if (job is { Type: "fiscal.print", JobId.Length: > 0 })
        {
            var result = await _processor.ProcessAsync(job, ct);
            await _reporter.ReportAsync(result, ct);
        }
        else if (job is not null)
        {
            _log.LogWarning("Ignoring message of unsupported type '{Type}'", job.Type);
        }

        // Advance the cursor once the event is handled (local store guarantees correctness on redelivery).
        if (!string.IsNullOrEmpty(eventId))
            await _store.SetCursorAsync(CursorKey, eventId, ct);
    }
}
