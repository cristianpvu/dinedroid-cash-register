using System.Text.Json;
using FiscalAgent.Contracts;
using FiscalAgent.Jobs;
using FiscalAgent.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiscalAgent.Cli;

/// <summary>
/// Sends a receipt described in a JSON file through the full pipeline to the configured
/// fiscal device. The JSON is an OrderPayload (the exact shape the backend will send),
/// so a successful print here proves the end-to-end mapping is correct.
/// Usage: dotnet run -- send-bon path\to\order.json
/// </summary>
public static class SendBon
{
    public static async Task RunAsync(IServiceProvider sp, string filePath)
    {
        var processor = sp.GetRequiredService<JobProcessor>();
        var store = sp.GetRequiredService<JobStore>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SendBon");

        await store.InitAsync();

        if (!File.Exists(filePath))
        {
            log.LogError("File not found: {Path}", filePath);
            return;
        }

        OrderPayload? order;
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            order = JsonSerializer.Deserialize<OrderPayload>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Invalid JSON in {Path}", filePath);
            return;
        }

        if (order is null || order.Lines.Count == 0)
        {
            log.LogError("Order has no lines.");
            return;
        }
        if (order.Payments.Count == 0)
        {
            log.LogError("Order has no payments.");
            return;
        }

        if (string.IsNullOrEmpty(order.OrderId))
            order.OrderId = "test-" + Guid.NewGuid().ToString("N")[..8];

        // Fresh jobId every run so the same file can be printed repeatedly (dedup wouldn't reprint).
        var msg = new JobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            IdempotencyKey = $"{order.OrderId}:{DateTime.UtcNow:yyyyMMddHHmmss}",
            RestaurantId = "test",
            IssuedAt = DateTimeOffset.UtcNow.ToString("O"),
            Order = order
        };

        log.LogInformation("Sending {Count} line(s) to fiscal device (jobId={JobId})...",
            order.Lines.Count, msg.JobId);

        var result = await processor.ProcessAsync(msg, CancellationToken.None);

        log.LogInformation("================ RESULT ================");
        log.LogInformation("status : {Status}", result.Status);
        log.LogInformation("bon    : {Bon}", result.BonNumber ?? "-");
        if (result.Error is not null)
            log.LogInformation("error  : {Code} / {Msg}", result.Error.Code, result.Error.Message);
        log.LogInformation("raw    : {Raw}", result.Raw ?? "-");
        log.LogInformation("========================================");
    }
}
