namespace FiscalAgent.Fiscal;

/// <summary>
/// Abstraction over the physical fiscal device. Implementations:
/// FiscalNetDevice (real, drives the casa de marcat) and FakeFiscalDevice (dev).
/// </summary>
public interface IFiscalDevice
{
    Task<FiscalResult> PrintReceiptAsync(ReceiptJob job, CancellationToken ct);

    /// <summary>
    /// Executes a single FiscalNet command string (e.g. "X^", "Z^", "I^5000", "VB^").
    /// POSTs ["command"] to /api/Receipt and parses the standard BONOK response.
    /// </summary>
    Task<FiscalResult> ExecuteSimpleAsync(string fiscalCommand, CancellationToken ct);
}
