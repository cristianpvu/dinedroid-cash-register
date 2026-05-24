using System.Net.Http.Headers;
using FiscalAgent.Cli;
using FiscalAgent.Configuration;
using FiscalAgent.Fiscal;
using FiscalAgent.Jobs;
using FiscalAgent.Storage;
using FiscalAgent.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "FiscalAgent");

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobProcessor>();
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
});
builder.Services.AddHttpClient(ResultReporter.HttpClientName, c =>
{
    ConfigureBackend(c, backend);
    c.Timeout = TimeSpan.FromSeconds(30);
});

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
