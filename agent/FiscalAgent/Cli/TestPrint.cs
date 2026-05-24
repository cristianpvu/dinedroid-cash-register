using FiscalAgent.Contracts;
using FiscalAgent.Jobs;
using FiscalAgent.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiscalAgent.Cli;

/// <summary>
/// Runs a sample receipt through the full pipeline (JobProcessor -> IFiscalDevice -> JobStore)
/// without needing the backend. With Driver=Fake it writes the command list to a file;
/// with Driver=FiscalNet it prints on the real cash register. Also demonstrates dedup.
/// </summary>
public static class TestPrint
{
    public static async Task RunAsync(IServiceProvider sp)
    {
        var processor = sp.GetRequiredService<JobProcessor>();
        var store = sp.GetRequiredService<JobStore>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TestPrint");

        await store.InitAsync();

        var jobId = Guid.NewGuid().ToString();
        var msg = new JobMessage
        {
            JobId = jobId,
            IdempotencyKey = $"test-order:{DateTime.UtcNow:yyyyMMddHHmmss}",
            RestaurantId = "demo-restaurant",
            IssuedAt = DateTimeOffset.UtcNow.ToString("O"),
            Order = new OrderPayload
            {
                OrderId = "demo-order",
                Table = "12",
                Waiter = "Andrei",
                Lines =
                {
                    new JobLine { Name = "Pizza Margherita", UnitPriceBani = 3500, QtyThousandths = 2000, VatGroup = 1 },
                    new JobLine { Name = "Coca-Cola 0.5L",   UnitPriceBani = 800,  QtyThousandths = 1000, VatGroup = 1 }
                },
                Payments =
                {
                    new JobPayment { Type = 1, AmountBani = 7800 } // cash, 78,00 RON
                }
            }
        };

        log.LogInformation("--- Test print, jobId={JobId} ---", jobId);
        var first = await processor.ProcessAsync(msg, CancellationToken.None);
        log.LogInformation("First run -> status={Status}, bon={Bon}, error={Err}",
            first.Status, first.BonNumber, first.Error?.Message);

        log.LogInformation("--- Re-processing the SAME job (dedup test) ---");
        var second = await processor.ProcessAsync(msg, CancellationToken.None);
        log.LogInformation("Second run -> status={Status}, bon={Bon}  (must match first, no reprint)",
            second.Status, second.BonNumber);

        if (first.Status == ResultStatus.Printed && second.BonNumber == first.BonNumber)
            log.LogInformation("OK: idempotency holds — same bon, printed once.");
        else
            log.LogWarning("Check: outcomes differ across runs.");
    }
}
