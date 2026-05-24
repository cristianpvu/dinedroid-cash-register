using System.Text;

namespace FiscalAgent.Fiscal;

/// <summary>
/// Translates a <see cref="ReceiptJob"/> into the FiscalNet command list.
/// Grammar (pipe-delimited, '^' separator):
///   CF^codFiscal
///   S^denumire^pretBani^cantitateMiimi^um^grupaTVA^grupaDep
///   P^tipPlata^valoareBani
/// Prices are bani (2 decimals, no separator); quantities are thousandths (3 decimals).
/// </summary>
public static class FiscalNetCommandBuilder
{
    public static IReadOnlyList<string> Build(ReceiptJob job)
    {
        if (job.Lines.Count == 0)
            throw new ArgumentException("Receipt has no lines.", nameof(job));
        if (job.Payments.Count == 0)
            throw new ArgumentException("Receipt has no payments.", nameof(job));

        var cmds = new List<string>();

        if (!string.IsNullOrWhiteSpace(job.FiscalCode))
            cmds.Add($"CF^{Clean(job.FiscalCode!)}");

        foreach (var l in job.Lines)
        {
            var unit = string.IsNullOrWhiteSpace(l.Unit) ? "buc" : Clean(l.Unit);
            cmds.Add($"S^{Clean(l.Name)}^{l.UnitPriceBani}^{l.QtyThousandths}^{unit}^{l.VatGroup}^{l.Department}");
        }

        // Free-text lines (TL) must come after the items and before the payments.
        if (!string.IsNullOrWhiteSpace(job.Table))
            cmds.Add($"TL^Masa: {Clean(job.Table!)}");
        if (!string.IsNullOrWhiteSpace(job.Waiter))
            cmds.Add($"TL^Ospatar: {Clean(job.Waiter!)}");

        foreach (var p in job.Payments)
            cmds.Add($"P^{(int)p.Type}^{p.AmountBani}");

        return cmds;
    }

    /// <summary>'^' is the field separator and newlines break the command file, so strip them from text.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch is '^' or '\r' or '\n' ? ' ' : ch);
        return sb.ToString().Trim();
    }
}
