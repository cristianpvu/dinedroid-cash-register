namespace FiscalAgent.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string AgentId { get; set; } = "";
    public string RestaurantId { get; set; } = "";
    public string DatabasePath { get; set; } = "data/fiscalagent.db";

    public FiscalOptions Fiscal { get; set; } = new();
    public BackendOptions Backend { get; set; } = new();
}

public sealed class FiscalOptions
{
    /// <summary>"Fake" for development, "FiscalNet" to drive the real cash register.</summary>
    public string Driver { get; set; } = "Fake";

    /// <summary>FiscalNet local REST server, e.g. http://localhost:65400</summary>
    public string BaseUrl { get; set; } = "http://localhost:65400";

    public int TimeoutSeconds { get; set; } = 60;

    public string FakeOutputDir { get; set; } = "data/fake-receipts";
}

public sealed class BackendOptions
{
    /// <summary>When false the agent runs without connecting to the cloud (test mode).</summary>
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string StreamPath { get; set; } = "/agent/stream";
    public string ResultPath { get; set; } = "/agent/jobs/{jobId}/result";
    public string CommandResultPath { get; set; } = "/agent/commands/{commandId}/result";
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Optional: connect to this IP directly while still using BaseUrl's hostname for TLS
    /// and the Host header. Lets the agent bypass a CDN/proxy (e.g. Cloudflare) without
    /// touching the hosts file or disabling certificate validation. Leave empty in production.
    /// </summary>
    public string? OriginIp { get; set; }
}
