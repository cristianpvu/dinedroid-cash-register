using FiscalAgent.Contracts;
using FiscalAgent.Fiscal;
using Microsoft.Extensions.Logging;

namespace FiscalAgent.Jobs;

/// <summary>
/// Executes one-shot fiscal commands received from the cloud:
/// X/Z reports, cash in/out, cancel blocked receipt, open drawer.
/// All commands use the same FiscalNet /api/Receipt endpoint with a single command string.
/// </summary>
public sealed class CommandProcessor
{
    private static readonly Dictionary<string, string> CommandMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xreport"]        = "X^",
        ["zreport"]        = "Z^",
        ["cashin"]         = "I^",   // appended with amountBani
        ["cashout"]        = "O^",   // appended with amountBani
        ["cancel_receipt"] = "VB^",
        ["open_drawer"]    = "DS^"
    };

    private readonly IFiscalDevice _device;
    private readonly ILogger<CommandProcessor> _log;

    public CommandProcessor(IFiscalDevice device, ILogger<CommandProcessor> log)
    {
        _device = device;
        _log = log;
    }

    public async Task<CommandResultMessage> ProcessAsync(CommandMessage msg, CancellationToken ct)
    {
        if (!CommandMap.TryGetValue(msg.Command, out var prefix))
        {
            _log.LogWarning("Unknown fiscal command: '{Command}'", msg.Command);
            return Fail(msg, "UNKNOWN_COMMAND", $"Unknown command: {msg.Command}");
        }

        var fiscalCommand = msg.AmountBani is > 0
            ? $"{prefix}{msg.AmountBani}"
            : prefix;

        _log.LogInformation("Executing fiscal command: {Cmd} (commandId={Id})", fiscalCommand, msg.CommandId);

        var r = await _device.ExecuteSimpleAsync(fiscalCommand, ct);

        if (r.Ok)
        {
            _log.LogInformation("Command '{Command}' succeeded (commandId={Id})", msg.Command, msg.CommandId);
            return new CommandResultMessage { CommandId = msg.CommandId, Status = "success", Raw = r.RawResponse };
        }

        _log.LogWarning("Command '{Command}' failed: {Code} — {Msg} (commandId={Id})",
            msg.Command, r.ErrorCode, r.ErrorMessage, msg.CommandId);
        return Fail(msg, r.ErrorCode ?? "ERROR", r.ErrorMessage ?? "Command failed", r.RawResponse);
    }

    private static CommandResultMessage Fail(CommandMessage msg, string code, string message, string? raw = null) =>
        new()
        {
            CommandId = msg.CommandId,
            Status = "failed",
            Raw = raw,
            Error = new JobError { Code = code, Message = message }
        };
}
