using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FiscalAgent.Cli;
using FiscalAgent.Configuration;
using FiscalAgent.Fiscal;
using FiscalAgent.Jobs;
using FiscalAgent.Storage;
using FiscalAgent.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Local, gitignored overrides (backend URL + FISCAL_API_KEY token live here, not in git).
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Fetch OriginIp from backend so all agents pick up VPS IP changes without local edits.
// Falls back to appsettings.local.json value if the fetch fails.
{
    var tempBackend = builder.Configuration
        .GetSection($"{AgentOptions.SectionName}:Backend").Get<BackendOptions>() ?? new();
    if (tempBackend.Enabled && !string.IsNullOrWhiteSpace(tempBackend.BaseUrl) && !string.IsNullOrWhiteSpace(tempBackend.Token))
    {
        var remoteIp = await TryFetchRemoteOriginIpAsync(tempBackend);
        if (remoteIp != null)
            builder.Configuration[$"{AgentOptions.SectionName}:Backend:OriginIp"] = remoteIp;
    }
}

builder.Services.AddWindowsService(o => o.ServiceName = "FiscalAgent");

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddSingleton<CommandProcessor>();
builder.Services.AddSingleton<ResultReporter>();

// --- Fiscal device: Fake for dev, FiscalNet to drive the real cash register ---
var fiscal = builder.Configuration
    .GetSection($"{AgentOptions.SectionName}:Fiscal").Get<FiscalOptions>() ?? new();

if (string.Equals(fiscal.Driver, "FiscalNet", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IFiscalDevice, FiscalNetDevice>(c =>
    {
        c.BaseAddress = new Uri(fiscal.BaseUrl);
        c.Timeout = TimeSpan.FromSeconds(fiscal.TimeoutSeconds);
    });
}
else
{
    builder.Services.AddSingleton<IFiscalDevice, FakeFiscalDevice>();
}

// --- Backend HTTP clients (SSE stream is long-lived; result POST is short) ---
var backend = builder.Configuration
    .GetSection($"{AgentOptions.SectionName}:Backend").Get<BackendOptions>() ?? new();

builder.Services.AddHttpClient(SseClient.HttpClientName, c =>
{
    ConfigureBackend(c, backend);
    c.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => CreateBackendHandler(backend));
builder.Services.AddHttpClient(ResultReporter.HttpClientName, c =>
{
    ConfigureBackend(c, backend);
    c.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => CreateBackendHandler(backend));

builder.Services.AddHostedService<SseClient>();

var host = builder.Build();

// --- Console test mode: print a sample bon through the real pipeline, then exit ---
if (args.Length > 0 && args[0].Equals("test-print", StringComparison.OrdinalIgnoreCase))
{
    await TestPrint.RunAsync(host.Services);
    return;
}

// --- Send a receipt from a JSON file (products/prices) to the configured device ---
if (args.Length > 0 && args[0].Equals("send-bon", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: send-bon <file.json>");
        return;
    }
    await SendBon.RunAsync(host.Services, args[1]);
    return;
}

await host.RunAsync();

static void ConfigureBackend(HttpClient c, BackendOptions backend)
{
    if (!string.IsNullOrWhiteSpace(backend.BaseUrl))
        c.BaseAddress = new Uri(backend.BaseUrl);
    if (!string.IsNullOrWhiteSpace(backend.Token))
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", backend.Token);
}

static async Task<string?> TryFetchRemoteOriginIpAsync(BackendOptions backend)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", backend.Token);
        var url = $"{backend.BaseUrl.TrimEnd('/')}/agent/config";
        var json = await http.GetFromJsonAsync<JsonElement>(url);
        if (json.TryGetProperty("originIp", out var ip) && ip.ValueKind == JsonValueKind.String)
        {
            var val = ip.GetString();
            if (!string.IsNullOrWhiteSpace(val))
                return val;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[warn] Remote config fetch failed: {ex.Message}. Using local OriginIp.");
    }
    return null;
}

static HttpMessageHandler CreateBackendHandler(BackendOptions backend)
{
    var handler = new SocketsHttpHandler();

    // When OriginIp is set, redirect the TCP connection to that IP while TLS/SNI and the
    // Host header still use BaseUrl's hostname (so the cert stays valid). Bypasses a CDN.
    if (!string.IsNullOrWhiteSpace(backend.OriginIp))
    {
        var ip = backend.OriginIp!;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(ip, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    return handler;
}
