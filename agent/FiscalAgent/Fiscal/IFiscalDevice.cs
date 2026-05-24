namespace FiscalAgent.Fiscal;

/// <summary>
/// Abstraction over the physical fiscal device. Implementations:
/// FiscalNetDevice (real, drives the casa de marcat) and FakeFiscalDevice (dev).
/// </summary>
public interface IFiscalDevice
{
    Task<FiscalResult> PrintReceiptAsync(ReceiptJob job, CancellationToken ct);
}
