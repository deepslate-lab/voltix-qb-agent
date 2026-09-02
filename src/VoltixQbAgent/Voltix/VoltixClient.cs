using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoltixQbAgent.Voltix;

/// <summary>
/// HTTP client for Voltix's /api/qb-agent/* endpoints. Every request carries
/// the pairing key in the x-agent-key header; that key opens only these
/// endpoints server-side.
/// </summary>
public sealed class VoltixClient : IDisposable
{
    private readonly HttpClient _http;

    public static string AgentVersion =>
        typeof(VoltixClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public VoltixClient(string baseUrl, string agentKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("x-agent-key", agentKey);
        _http.DefaultRequestHeaders.Add("User-Agent", $"VoltixQbAgent/{AgentVersion}");
    }

    public async Task<HandshakeResponse> HandshakeAsync(CancellationToken ct = default)
    {
        return await PostAsync<HandshakeResponse>("api/qb-agent/handshake",
            new { agent_version = AgentVersion }, ct);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(bool? qbOpen, string? companyName, CancellationToken ct = default)
    {
        return await PostAsync<HeartbeatResponse>("api/qb-agent/heartbeat",
            new { agent_version = AgentVersion, qb_open = qbOpen, company_name = companyName }, ct);
    }

    public async Task<PullPlanResponse> GetPullPlanAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/qb-agent/pull-plan", ct);
        return await ReadAsync<PullPlanResponse>(resp, ct);
    }

    public async Task AdvanceSyncStateAsync(string entity, DateTimeOffset? watermark, string? result, CancellationToken ct = default)
    {
        await PostAsync<BaseResponse>("api/qb-agent/sync-state",
            new { entity, watermark = watermark?.UtcDateTime.ToString("o"), result }, ct);
    }

    public async Task<WorkClaimResponse> ClaimWorkAsync(int max, CancellationToken ct = default)
    {
        return await PostAsync<WorkClaimResponse>("api/qb-agent/work/claim", new { max }, ct);
    }

    public async Task ReportWorkResultAsync(string jobId, bool success, object? result, string? error, CancellationToken ct = default)
    {
        await PostAsync<BaseResponse>($"api/qb-agent/work/{jobId}/result",
            new { success, result, error }, ct);
    }

    public async Task UpsertRowsAsync(string entity, IEnumerable<Dictionary<string, object?>> rows, CancellationToken ct = default)
    {
        await PostAsync<BaseResponse>($"api/qb-agent/upsert/{entity}", new { rows }, ct);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct) where T : BaseResponse
    {
        var resp = await _http.PostAsJsonAsync(path, body, ct);
        return await ReadAsync<T>(resp, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : BaseResponse
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new VoltixApiException("Voltix rejected the agent key. Regenerate the key in Voltix and pair again.", unauthorized: true);

        var text = await resp.Content.ReadAsStringAsync(ct);
        T? parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(text);
        }
        catch (JsonException)
        {
            // fall through to the generic error below
        }
        if (!resp.IsSuccessStatusCode || parsed is null || !parsed.Success)
        {
            var detail = parsed?.Error ?? $"HTTP {(int)resp.StatusCode}";
            throw new VoltixApiException($"Voltix API error: {detail}");
        }
        return parsed;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class VoltixApiException : Exception
{
    public bool Unauthorized { get; }
    public VoltixApiException(string message, bool unauthorized = false) : base(message)
    {
        Unauthorized = unauthorized;
    }
}

public class BaseResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class HandshakeResponse : BaseResponse
{
    [JsonPropertyName("tenant_name")] public string TenantName { get; set; } = "";
    [JsonPropertyName("expected_company_name")] public string? ExpectedCompanyName { get; set; }
    [JsonPropertyName("expected_company_file")] public string? ExpectedCompanyFile { get; set; }
    [JsonPropertyName("config")] public AgentServerConfig Config { get; set; } = new();
}

public sealed class HeartbeatResponse : BaseResponse
{
    [JsonPropertyName("config")] public AgentServerConfig Config { get; set; } = new();
    [JsonPropertyName("outbox_pending")] public int OutboxPending { get; set; }
}

public sealed class AgentServerConfig
{
    [JsonPropertyName("poll_interval_seconds")] public int PollIntervalSeconds { get; set; } = 15;
    [JsonPropertyName("batch_size")] public int BatchSize { get; set; } = 50;
}

public sealed class PullPlanResponse : BaseResponse
{
    [JsonPropertyName("batch_size")] public int BatchSize { get; set; }
    [JsonPropertyName("entities")] public List<PullEntity> Entities { get; set; } = new();
}

public sealed class PullEntity
{
    [JsonPropertyName("entity")] public string Entity { get; set; } = "";
    [JsonPropertyName("watermark")] public DateTimeOffset? Watermark { get; set; }
    [JsonPropertyName("last_run_at")] public DateTimeOffset? LastRunAt { get; set; }
}

public sealed class WorkClaimResponse : BaseResponse
{
    [JsonPropertyName("jobs")] public List<WorkJob> Jobs { get; set; } = new();
}

public sealed class WorkJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
}
