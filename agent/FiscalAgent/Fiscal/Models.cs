namespace FiscalAgent.Fiscal;

/// <summary>FiscalNet payment types (P^Type^Value).</summary>
public enum PaymentType
{
    Cash = 1,
    Card = 2,
    Credit = 3,
    MealVoucher = 4,
    ValueVoucher = 5,
    Voucher = 6,
    Modern = 7,
    Other = 8
}

/// <summary>Device-facing receipt model — decoupled from the wire contract.</summary>
public sealed class ReceiptJob
{
    public string? FiscalCode { get; init; }

    /// <summary>Free-text lines printed on the bon (e.g. "Masa: 5", "Ospatar: Andrei").</summary>
    public string? Table { get; init; }
    public string? Waiter { get; init; }

    public IReadOnlyList<ReceiptLine> Lines { get; init; } = Array.Empty<ReceiptLine>();
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = Array.Empty<ReceiptPayment>();
}

public sealed class ReceiptLine
{
    public string Name { get; init; } = "";
    public long UnitPriceBani { get; init; }
    public long QtyThousandths { get; init; } = 1000;
    public string Unit { get; init; } = "buc";
    public int VatGroup { get; init; } = 1;
    public int Department { get; init; } = 1;
}

public sealed class ReceiptPayment
{
    public PaymentType Type { get; init; } = PaymentType.Cash;
    public long AmountBani { get; init; }
}

/// <summary>
/// Outcome of a fiscal print attempt.
/// <para><b>Ok</b>: confirmed printed.</para>
/// <para><b>Uncertain</b>: we do NOT know whether the bon was emitted (lost connection,
/// timeout, unparseable response). Must not be auto-retried — needs human verification.</para>
/// <para>Otherwise: a definite failure (device said no) — safe to retry.</para>
/// </summary>
public sealed record FiscalResult(
    bool Ok,
    bool Uncertain,
    string? BonNumber,
    string? RawResponse,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static FiscalResult Success(string? bonNumber, string? raw) =>
        new(true, false, bonNumber, raw, null, null);

    public static FiscalResult Failure(string code, string message, string? raw = null) =>
        new(false, false, null, raw, code, message);

    public static FiscalResult Unknown(string code, string message, string? raw = null) =>
        new(false, true, null, raw, code, message);
}
