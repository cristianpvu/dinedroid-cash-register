using FiscalAgent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FiscalAgent.Storage;

public static class JobStatus
{
    public const string Printing = "printing";
    public const string Printed = "printed";
    public const string Failed = "failed";
}

public sealed record JobRecord(
    string JobId,
    string IdempotencyKey,
    string Status,
    string? BonNumber,
    string? Raw,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Local durable state for idempotency. The agent-side guarantee against double-printing:
/// a job is marked 'printing' BEFORE the device call, so a crash leaves a trace.
/// </summary>
public sealed class JobStore
{
    private readonly string _connectionString;

    public JobStore(IOptions<AgentOptions> options)
    {
        var path = options.Value.DatabasePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS jobs (
                job_id          TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL,
                status          TEXT NOT NULL,
                bon_number      TEXT,
                raw             TEXT,
                error_code      TEXT,
                error_message   TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<JobRecord?> GetAsync(string jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT job_id, idempotency_key, status, bon_number, raw, error_code, error_message
            FROM jobs WHERE job_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", jobId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new JobRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>Atomically claim a job for printing. Returns true if this is a NEW job.</summary>
    public async Task<bool> TryBeginPrintAsync(string jobId, string idempotencyKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (job_id, idempotency_key, status, created_at, updated_at)
            VALUES ($id, $key, $status, $now, $now)
            ON CONFLICT(job_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$key", idempotencyKey);
        cmd.Parameters.AddWithValue("$status", JobStatus.Printing);
        cmd.Parameters.AddWithValue("$now", Now());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task MarkPrintedAsync(string jobId, string? bonNumber, string? raw, CancellationToken ct = default) =>
        UpdateResultAsync(jobId, JobStatus.Printed, bonNumber, raw, null, null, ct);

    public Task MarkFailedAsync(string jobId, string code, string message, string? raw, CancellationToken ct = default) =>
        UpdateResultAsync(jobId, JobStatus.Failed, null, raw, code, message, ct);

    private async Task UpdateResultAsync(
        string jobId, string status, string? bon, string? raw, string? code, string? msg, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs
            SET status = $status, bon_number = $bon, raw = $raw,
                error_code = $code, error_message = $msg, updated_at = $now
            WHERE job_id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$bon", (object?)bon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raw", (object?)raw ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", (object?)msg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetCursorAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetCursorAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
