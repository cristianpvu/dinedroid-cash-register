using FiscalAgent.Contracts;
using FiscalAgent.Fiscal;
using FiscalAgent.Storage;
using Microsoft.Extensions.Logging;

namespace FiscalAgent.Jobs;

public static class ResultStatus
{
    public const string Printed = "printed";
    public const string Failed = "failed";          // device said no — safe to retry with a new jobId
    public const string NeedsReview = "needs_review"; // outcome unknown — human must verify, do NOT auto-retry
}

/// <summary>
/// The heart of the agent. Guarantees a bon is never printed twice for the same jobId,
/// and never blindly retried when the outcome is unknown.
/// Returns the <see cref="ResultMessage"/> to report; the transport handles delivery.
/// </summary>
public sealed class JobProcessor
{
    private readonly JobStore _store;
    private readonly IFiscalDevice _device;
    private readonly ILogger<JobProcessor> _log;

    public JobProcessor(JobStore store, IFiscalDevice device, ILogger<JobProcessor> log)
    {
        _store = store;
        _device = device;
        _log = log;
    }

    public async Task<ResultMessage> ProcessAsync(JobMessage msg, CancellationToken ct)
    {
        // 1. Dedup against local state (handles at-least-once redelivery).
        var existing = await _store.GetAsync(msg.JobId, ct);
        if (existing is not null)
        {
            switch (existing.Status)
            {
                case JobStatus.Printed:
                    _log.LogInformation("Job {Id} already printed (bon {Bon}); re-reporting, NOT reprinting.",
                        msg.JobId, existing.BonNumber);
                    return Result(msg, ResultStatus.Printed, existing.BonNumber, existing.Raw, null, null);

                case JobStatus.Failed:
                    return Result(msg, ResultStatus.Failed, null, existing.Raw,
                        existing.ErrorCode ?? "FAILED", existing.ErrorMessage ?? "previously failed");

                case JobStatus.Printing:
                    // Interrupted mid-print (e.g. power loss). We can't know if the bon came out.
                    _log.LogError("Job {Id} stuck in 'printing' — needs manual verification, NOT reprinting.", msg.JobId);
                    return Result(msg, ResultStatus.NeedsReview, null, existing.Raw,
                        "INTERRUPTED", "Print was interrupted; verify on the cash register before retrying.");
            }
        }

        // 2. Claim the job atomically before touching the device.
        if (!await _store.TryBeginPrintAsync(msg.JobId, msg.IdempotencyKey, ct))
        {
            var now = await _store.GetAsync(msg.JobId, ct);
            if (now?.Status == JobStatus.Printed)
                return Result(msg, ResultStatus.Printed, now.BonNumber, now.Raw, null, null);
            return Result(msg, ResultStatus.NeedsReview, null, null, "IN_PROGRESS", "Job is already being processed.");
        }

        // 3. Map + validate the receipt.
        ReceiptJob receipt;
        try
        {
            receipt = Map(msg.Order);
        }
        catch (Exception ex)
        {
            await _store.MarkFailedAsync(msg.JobId, "INVALID_JOB", ex.Message, null, ct);
            return Result(msg, ResultStatus.Failed, null, null, "INVALID_JOB", ex.Message);
        }

        // 4. Print.
        var r = await _device.PrintReceiptAsync(receipt, ct);

        if (r.Ok)
        {
            await _store.MarkPrintedAsync(msg.JobId, r.BonNumber, r.RawResponse, ct);
            _log.LogInformation("Job {Id} printed: bon {Bon}", msg.JobId, r.BonNumber);
            return Result(msg, ResultStatus.Printed, r.BonNumber, r.RawResponse, null, null);
        }

        if (r.Uncertain)
        {
            // Leave the row in 'printing' so any redelivery hits the NeedsReview branch above.
            _log.LogError("Job {Id} UNCERTAIN ({Code}): {Msg}. Left for manual review.",
                msg.JobId, r.ErrorCode, r.ErrorMessage);
            return Result(msg, ResultStatus.NeedsReview, null, r.RawResponse,
                r.ErrorCode ?? "UNCERTAIN", r.ErrorMessage ?? "Outcome unknown");
        }

        // Definite failure — device positively did not print. Safe to retry with a new jobId.
        await _store.MarkFailedAsync(msg.JobId, r.ErrorCode ?? "ERROR", r.ErrorMessage ?? "print failed", r.RawResponse, ct);
        _log.LogWarning("Job {Id} failed ({Code}): {Msg}", msg.JobId, r.ErrorCode, r.ErrorMessage);
        return Result(msg, ResultStatus.Failed, null, r.RawResponse, r.ErrorCode ?? "ERROR", r.ErrorMessage ?? "print failed");
    }

    private static ReceiptJob Map(OrderPayload o)
    {
        var lines = o.Lines.Select(l => new ReceiptLine
        {
            Name = l.Name,
            UnitPriceBani = l.UnitPriceBani,
            QtyThousandths = l.QtyThousandths,
            Unit = l.Unit,
            VatGroup = l.VatGroup,
            Department = l.Department
        }).ToList();

        var payments = o.Payments.Select(p => new ReceiptPayment
        {
            Type = (PaymentType)p.Type,
            AmountBani = p.AmountBani
        }).ToList();

        return new ReceiptJob
        {
            FiscalCode = o.FiscalCode,
            Table = o.Table,
            Waiter = o.Waiter,
            Lines = lines,
            Payments = payments
        };
    }

    private static ResultMessage Result(
        JobMessage msg, string status, string? bon, string? raw, string? code, string? errMsg) =>
        new()
        {
            JobId = msg.JobId,
            IdempotencyKey = msg.IdempotencyKey,
            Status = status,
            BonNumber = bon,
            Raw = raw,
            Error = code is null ? null : new JobError { Code = code, Message = errMsg ?? "" },
            CompletedAt = DateTimeOffset.UtcNow.ToString("O")
        };
}
