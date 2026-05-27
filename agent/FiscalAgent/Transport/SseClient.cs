using System.Diagnostics;
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

        var versionFile = Path.Combine(AppContext.BaseDirectory, "version.txt");
        var agentVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "unknown";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(agentVersion, ct);
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

    private async Task ConnectAndListenAsync(string agentVersion, CancellationToken ct)
    {
        var client = _factory.CreateClient(HttpClientName);

        var path = _opts.Backend.StreamPath;
        string streamUrl;
        if (path.Contains("restaurantId=", StringComparison.OrdinalIgnoreCase))
            streamUrl = path;
        else
        {
            var sep = path.Contains('?') ? '&' : '?';
            streamUrl = $"{path}{sep}restaurantId={Uri.EscapeDataString(_opts.RestaurantId)}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        req.Headers.TryAddWithoutValidation("X-Agent-Version", agentVersion);

        var lastId = await _store.GetCursorAsync(CursorKey, ct);
        if (!string.IsNullOrEmpty(lastId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastId);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        _log.LogInformation("SSE connected ({Url}), resuming from id={LastId}",
            streamUrl, lastId ?? "<start>");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventId   = null;
        string? eventType = null;
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // server closed the stream

            if (line.Length == 0)
            {
                if (data.Length > 0 || eventType != null)
                    await HandleEventAsync(eventId, eventType, data.ToString(), ct);
                eventId   = null;
                eventType = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':') continue; // comment / heartbeat ping

            if (line.StartsWith("id:", StringComparison.Ordinal))
                eventId = line[3..].Trim();
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' ')) value = value[1..];
                data.Append(value).Append('\n');
            }
            // "retry:" intentionally ignored.
        }
    }

    private async Task HandleEventAsync(string? eventId, string? eventType, string data, CancellationToken ct)
    {
        // Remote update signal: spawn updater.ps1 as a detached process (it will stop the
        // service, replace files, and restart — this process will die when the service stops).
        if (string.Equals(eventType, "update", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("Remote update signal received. Launching updater...");
            LaunchUpdater();
            if (!string.IsNullOrEmpty(eventId))
                await _store.SetCursorAsync(CursorKey, eventId, ct);
            return;
        }

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

        if (!string.IsNullOrEmpty(eventId))
            await _store.SetCursorAsync(CursorKey, eventId, ct);
    }

    private void LaunchUpdater()
    {
        var updaterPath = Path.Combine(AppContext.BaseDirectory, "updater.ps1");
        if (!File.Exists(updaterPath))
        {
            _log.LogWarning("updater.ps1 not found at {Path} — cannot self-update", updaterPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{updaterPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to launch updater.ps1");
        }
    }
}
