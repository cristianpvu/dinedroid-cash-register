using FiscalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiscalAgent.Fiscal;

/// <summary>
/// Development device. Writes the exact FiscalNet command list it would send to a file
/// and returns a fake bon number, so the whole chain runs without a real cash register.
/// </summary>
public sealed class FakeFiscalDevice : IFiscalDevice
{
    private readonly ILogger<FakeFiscalDevice> _log;
    private readonly string _outputDir;

    public FakeFiscalDevice(IOptions<AgentOptions> options, ILogger<FakeFiscalDevice> log)
    {
        _log = log;
        _outputDir = options.Value.Fiscal.FakeOutputDir;
    }

    public async Task<FiscalResult> PrintReceiptAsync(ReceiptJob job, CancellationToken ct)
    {
        var commands = FiscalNetCommandBuilder.Build(job);
        Directory.CreateDirectory(_outputDir);

        var bon = $"FAKE-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var path = Path.Combine(_outputDir, bon + ".txt");
        await File.WriteAllLinesAsync(path, commands, ct);

        _log.LogInformation("[FAKE] bon {Bon} written to {Path}\n{Commands}",
            bon, path, string.Join(Environment.NewLine, commands));

        return FiscalResult.Success(bon, string.Join("\n", commands));
    }

    public Task<FiscalResult> ExecuteSimpleAsync(string fiscalCommand, CancellationToken ct)
    {
        _log.LogInformation("[FAKE] ExecuteSimple: {Cmd}", fiscalCommand);
        return Task.FromResult(FiscalResult.Success(null, $"FAKE OK: {fiscalCommand}"));
    }
}
