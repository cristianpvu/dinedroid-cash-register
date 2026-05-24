using System.Text.Json.Serialization;

namespace FiscalAgent.Contracts;

/// <summary>Wire contract v1 — what the cloud backend sends down to the agent.</summary>
public sealed class JobMessage
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("type")] public string Type { get; set; } = "fiscal.print";
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
    [JsonPropertyName("idempotencyKey")] public string IdempotencyKey { get; set; } = "";
    [JsonPropertyName("restaurantId")] public string RestaurantId { get; set; } = "";
    [JsonPropertyName("issuedAt")] public string IssuedAt { get; set; } = "";
    [JsonPropertyName("order")] public OrderPayload Order { get; set; } = new();
}

public sealed class OrderPayload
{
    [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";

    /// <summary>Optional client fiscal code (CUI) to print on the receipt.</summary>
    [JsonPropertyName("fiscalCode")] public string? FiscalCode { get; set; }

    [JsonPropertyName("lines")] public List<JobLine> Lines { get; set; } = new();
    [JsonPropertyName("payments")] public List<JobPayment> Payments { get; set; } = new();
    [JsonPropertyName("table")] public string? Table { get; set; }
    [JsonPropertyName("waiter")] public string? Waiter { get; set; }
}

public sealed class JobLine
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Unit price in bani (minor units). 600 = 6,00 RON. Never a float.</summary>
    [JsonPropertyName("unitPriceBani")] public long UnitPriceBani { get; set; }

    /// <summary>Quantity in thousandths. 1000 = 1 buc, 500 = 0,5.</summary>
    [JsonPropertyName("qtyThousandths")] public long QtyThousandths { get; set; } = 1000;

    [JsonPropertyName("unit")] public string Unit { get; set; } = "buc";

    /// <summary>FiscalNet VAT group (1..5).</summary>
    [JsonPropertyName("vatGroup")] public int VatGroup { get; set; } = 1;

    /// <summary>FiscalNet department/article group (1..n), 1 if not used.</summary>
    [JsonPropertyName("department")] public int Department { get; set; } = 1;
}

public sealed class JobPayment
{
    /// <summary>FiscalNet payment type: 1=Numerar, 2=Card, 3=Credit, 4=Tichet masa, ...</summary>
    [JsonPropertyName("type")] public int Type { get; set; } = 1;

    /// <summary>Amount in bani. 1000 = 10,00 RON.</summary>
    [JsonPropertyName("amountBani")] public long AmountBani { get; set; }
}

/// <summary>What the agent reports back to the cloud once printing is resolved.</summary>
public sealed class ResultMessage
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
    [JsonPropertyName("idempotencyKey")] public string IdempotencyKey { get; set; } = "";

    /// <summary>"printed" or "failed".</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";

    [JsonPropertyName("bonNumber")] public string? BonNumber { get; set; }
    [JsonPropertyName("raw")] public string? Raw { get; set; }
    [JsonPropertyName("error")] public JobError? Error { get; set; }
    [JsonPropertyName("completedAt")] public string CompletedAt { get; set; } = "";
}

public sealed class JobError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}
