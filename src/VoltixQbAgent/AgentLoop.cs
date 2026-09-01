using VoltixQbAgent.QuickBooks;
using VoltixQbAgent.Voltix;

namespace VoltixQbAgent;

/// <summary>
/// The background loop: heartbeat to Voltix every poll interval, and a QB
/// probe (short session + CompanyQuery identity gate) every ~60s. Skeleton
/// phase — pulls and outbox execution slot in behind the gate later.
///
/// All QBFC calls run on a dedicated STA thread (in-process COM).
/// </summary>
public sealed class AgentLoop
{
    private readonly AppConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public event Action? StateChanged;

    public string StatusText { get; private set; } = "Stopped";
    public string? TenantName { get; private set; }
    public string? ExpectedCompanyName { get; private set; }
    public string? LastCompanySeen { get; private set; }
    public bool QbOpen { get; private set; }
    public bool CompanyGateOk { get; private set; }
    public int OutboxPending { get; private set; }
    public bool Running => _task is { IsCompleted: false };

    private DateTime _lastQbProbe = DateTime.MinValue;
    private static readonly TimeSpan QbProbeInterval = TimeSpan.FromSeconds(60);

    public AgentLoop(AppConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Log.Info("Agent loop started.");
        SetStatus("Connecting…");
        using var client = new VoltixClient(_config.VoltixUrl, _config.AgentKey);

        var pollSeconds = 15;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // QB probe (short session, then closed) at most once per minute.
                if (DateTime.UtcNow - _lastQbProbe > QbProbeInterval)
                {
                    ProbeQuickBooks();
                    _lastQbProbe = DateTime.UtcNow;
                }

                var beat = await client.HeartbeatAsync(QbOpen, LastCompanySeen, ct);
                pollSeconds = Math.Max(5, beat.Config.PollIntervalSeconds);
                OutboxPending = beat.OutboxPending;

                if (TenantName is null)
                {
                    var hs = await client.HandshakeAsync(ct);
                    TenantName = hs.TenantName;
                    ExpectedCompanyName = hs.ExpectedCompanyName;
                    Log.Info($"Paired with tenant \"{hs.TenantName}\" (expected company: {hs.ExpectedCompanyName ?? "not set"}).");
                }

                EvaluateCompanyGate();
                SetStatus(QbOpen
                    ? (CompanyGateOk ? "Connected — QuickBooks OK" : "Connected — WRONG COMPANY FILE")
                    : "Connected — QuickBooks not reachable");

                // Phase Q2/Q3 slot in HERE, only when the gate passes:
                //   if (CompanyGateOk) { await RunPullsAsync(...); await DrainOutboxAsync(...); }
            }
            catch (VoltixApiException ex)
            {
                Log.Error(ex.Message);
                SetStatus(ex.Unauthorized ? "Key rejected — re-pair the agent" : "Voltix unreachable");
                if (ex.Unauthorized)
                {
                    // No point hammering with a dead key; wait for the user.
                    await DelaySafe(TimeSpan.FromMinutes(2), ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"Loop error: {ex.Message}");
                SetStatus("Error — see log");
            }

            await DelaySafe(TimeSpan.FromSeconds(pollSeconds), ct);
        }
        SetStatus("Stopped");
        Log.Info("Agent loop stopped.");
    }

    /// <summary>Open a short QB session on an STA thread, run CompanyQuery,
    /// close. Busy responses are just noted — the next probe retries.</summary>
    private void ProbeQuickBooks()
    {
        var result = RunSta(() =>
        {
            using var session = QbSession.Open(
                string.IsNullOrWhiteSpace(_config.CompanyFilePath) ? null : _config.CompanyFilePath);
            return session.QueryCompany();
        });

        if (result.Error is null)
        {
            QbOpen = true;
            LastCompanySeen = result.Value!.CompanyName;
        }
        else
        {
            QbOpen = false;
            Log.Warn($"QuickBooks probe: {result.Error}");
        }
        StateChanged?.Invoke();
    }

    private void EvaluateCompanyGate()
    {
        // No expectation configured yet -> gate passes but pulls stay off
        // server-side until the admin sets it. Wrong company -> hard stop.
        if (string.IsNullOrWhiteSpace(ExpectedCompanyName))
        {
            CompanyGateOk = QbOpen;
            return;
        }
        CompanyGateOk = QbOpen && string.Equals(
            LastCompanySeen?.Trim(), ExpectedCompanyName.Trim(), StringComparison.OrdinalIgnoreCase);
        if (QbOpen && !CompanyGateOk)
        {
            Log.Error($"Company gate FAILED: QuickBooks has \"{LastCompanySeen}\" open but this pairing expects \"{ExpectedCompanyName}\". Nothing will sync.");
        }
    }

    private static (T? Value, string? Error) RunSta<T>(Func<T> work) where T : class
    {
        T? value = null;
        string? error = null;
        var thread = new Thread(() =>
        {
            try { value = work(); }
            catch (QbAgentException ex) { error = ex.Message; }
            catch (Exception ex) { error = ex.Message; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return (value, error);
    }

    private void SetStatus(string text)
    {
        if (StatusText == text) return;
        StatusText = text;
        StateChanged?.Invoke();
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* stopping */ }
    }
}
