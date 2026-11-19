using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using FiscalAgent.Configuration;
using FiscalAgent.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FiscalAgent.Jobs;

public sealed class LabelProcessor
{
    private const int RecentCapacity = 500;

    private readonly LabelOptions _opts;
    private readonly ILogger<LabelProcessor> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LinkedList<string> _recentOrder = new();
    private readonly HashSet<string> _recent = new();

    public LabelProcessor(IOptions<AgentOptions> opts, ILogger<LabelProcessor> log)
    {
        _opts = opts.Value.Labels;
        _log = log;
    }

    public async Task<LabelResultMessage?> ProcessAsync(LabelMessage msg, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_recent.Contains(msg.JobId))
            {
                _log.LogInformation("Label job {JobId} already handled, skipping", msg.JobId);
                return null;
            }
            Remember(msg.JobId);

            var printer = string.IsNullOrWhiteSpace(msg.Printer) ? _opts.DefaultPrinter : msg.Printer;
            if (string.IsNullOrWhiteSpace(msg.Data))
                return Fail(msg, "EMPTY", "Label job has no data");

            var bytes = Encoding.UTF8.GetBytes(msg.Data);
            try
            {
                if (string.IsNullOrWhiteSpace(printer))
                    await WriteToFileAsync(msg, bytes, ct);
                else if (printer.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
                    await SendTcpAsync(printer, bytes, ct);
                else if (OperatingSystem.IsWindows())
                    RawPrinter.Send(printer, $"DineDroid label {msg.JobId}", bytes);
                else
                    return Fail(msg, "UNSUPPORTED", "Windows printer names are only supported on Windows");

                _log.LogInformation("Printed {Count} label(s) for job {JobId} on '{Printer}'", msg.Count, msg.JobId, string.IsNullOrWhiteSpace(printer) ? "file" : printer);
                return new LabelResultMessage { JobId = msg.JobId, Status = "printed" };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Label job {JobId} failed on '{Printer}'", msg.JobId, printer);
                return Fail(msg, "PRINT_ERROR", ex.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Remember(string jobId)
    {
        _recent.Add(jobId);
        _recentOrder.AddLast(jobId);
        while (_recentOrder.Count > RecentCapacity)
        {
            _recent.Remove(_recentOrder.First!.Value);
            _recentOrder.RemoveFirst();
        }
    }

    private async Task SendTcpAsync(string printer, byte[] bytes, CancellationToken ct)
    {
        var uri = new Uri(printer);
        var port = uri.Port > 0 ? uri.Port : 9100;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(bytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }

    private async Task WriteToFileAsync(LabelMessage msg, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.IsPathRooted(_opts.FakeOutputDir)
            ? _opts.FakeOutputDir
            : Path.Combine(AppContext.BaseDirectory, _opts.FakeOutputDir);
        Directory.CreateDirectory(dir);
        var ext = string.Equals(msg.Language, "TSPL", StringComparison.OrdinalIgnoreCase) ? "tspl" : "zpl";
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{msg.JobId}.{ext}"), bytes, ct);
    }

    private static LabelResultMessage Fail(LabelMessage msg, string code, string message) =>
        new()
        {
            JobId = msg.JobId,
            Status = "failed",
            Error = new JobError { Code = code, Message = message }
        };
}

internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName = "";
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);

    public static void Send(string printerName, string docName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new InvalidOperationException($"Cannot open printer '{printerName}' (error {Marshal.GetLastWin32Error()})");

        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            if (StartDocPrinter(handle, 1, new DocInfo { DocName = docName }) == 0)
                throw new InvalidOperationException($"StartDocPrinter failed (error {Marshal.GetLastWin32Error()})");
            try
            {
                if (!StartPagePrinter(handle))
                    throw new InvalidOperationException($"StartPagePrinter failed (error {Marshal.GetLastWin32Error()})");
                try
                {
                    if (!WritePrinter(handle, buffer, bytes.Length, out var written) || written != bytes.Length)
                        throw new InvalidOperationException($"WritePrinter failed (error {Marshal.GetLastWin32Error()})");
                }
                finally
                {
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            ClosePrinter(handle);
        }
    }
}
