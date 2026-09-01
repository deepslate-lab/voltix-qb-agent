using VoltixQbAgent.Voltix;

namespace VoltixQbAgent.Forms;

/// <summary>
/// Status + pairing window. The app lives in the tray; closing this window
/// hides it (File → Exit or the tray menu actually quits).
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private AgentLoop? _loop;
    private bool _reallyClosing;

    private readonly TextBox _urlBox = new() { Width = 340 };
    private readonly TextBox _keyBox = new() { Width = 340, UseSystemPasswordChar = true };
    private readonly TextBox _companyFileBox = new() { Width = 340 };
    private readonly Button _pairButton = new() { Text = "Save && Test pairing", AutoSize = true };
    private readonly Button _startStopButton = new() { Text = "Start", AutoSize = true, Enabled = false };
    private readonly Label _statusLabel = new() { Text = "Stopped", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
    private readonly Label _detailLabel = new() { Text = "", AutoSize = true };
    private readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f),
        WordWrap = false,
    };
    private readonly NotifyIcon _tray = new();

    public MainForm(AppConfig config)
    {
        _config = config;
        Text = $"Voltix QB Agent v{VoltixClient.AgentVersion}";
        Width = 760;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(BuildLayout());
        SetupTray();

        _urlBox.Text = config.VoltixUrl;
        _keyBox.Text = config.AgentKey;
        _companyFileBox.Text = config.CompanyFilePath;

        _pairButton.Click += async (_, _) => await PairAsync();
        _startStopButton.Click += (_, _) => ToggleLoop();

        Log.LineWritten += OnLogLine;
        _logBox.Text = string.Join(Environment.NewLine, Log.Snapshot());

        // Auto-start when already paired (the normal boot path on the VM).
        if (config.IsPaired)
        {
            _startStopButton.Enabled = true;
            ToggleLoop();
        }
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pairing = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        void AddRow(string label, Control control)
        {
            pairing.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) });
            pairing.Controls.Add(control);
        }
        AddRow("Voltix URL", _urlBox);
        AddRow("Agent key", _keyBox);
        AddRow("Company file (optional)", _companyFileBox);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_pairButton);
        buttons.Controls.Add(_startStopButton);
        pairing.Controls.Add(new Label());
        pairing.Controls.Add(buttons);

        var status = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 8) };
        status.Controls.Add(_statusLabel);
        status.Controls.Add(_detailLabel);

        root.Controls.Add(pairing);
        root.Controls.Add(status);
        root.Controls.Add(_logBox);
        return root;
    }

    private void SetupTray()
    {
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "Voltix QB Agent";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _reallyClosing = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task PairAsync()
    {
        var url = _urlBox.Text.Trim();
        var key = _keyBox.Text.Trim();
        if (url.Length == 0 || key.Length == 0)
        {
            MessageBox.Show(this, "Enter the Voltix URL and the agent key generated in Voltix → Settings → Integrations → QuickBooks Desktop.", "Pairing", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _pairButton.Enabled = false;
        try
        {
            using var client = new VoltixClient(url, key);
            var hs = await client.HandshakeAsync();
            _config.VoltixUrl = url;
            _config.AgentKey = key;
            _config.CompanyFilePath = _companyFileBox.Text.Trim();
            _config.Save();
            Log.Info($"Pairing verified: tenant \"{hs.TenantName}\".");
            MessageBox.Show(this,
                $"Paired with tenant:\n\n    {hs.TenantName}\n\nExpected company file: {hs.ExpectedCompanyName ?? "(not set in Voltix yet)"}",
                "Pairing successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _startStopButton.Enabled = true;
            if (_loop is null || !_loop.Running) ToggleLoop();
        }
        catch (Exception ex)
        {
            Log.Error($"Pairing failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Pairing failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _pairButton.Enabled = true;
        }
    }

    private void ToggleLoop()
    {
        if (_loop is { Running: true })
        {
            _loop.Stop();
            _startStopButton.Text = "Start";
            return;
        }
        _loop = new AgentLoop(_config);
        _loop.StateChanged += () => BeginInvoke(RefreshStatus);
        _loop.Start();
        _startStopButton.Text = "Stop";
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_loop is null) return;
        _statusLabel.Text = _loop.StatusText;
        var parts = new List<string>();
        if (_loop.TenantName != null) parts.Add($"Tenant: {_loop.TenantName}");
        if (_loop.LastCompanySeen != null) parts.Add($"QB company: {_loop.LastCompanySeen}");
        if (_loop.OutboxPending > 0) parts.Add($"Outbox: {_loop.OutboxPending} pending");
        _detailLabel.Text = string.Join("   ·   ", parts);
        _tray.Text = $"Voltix QB Agent — {_loop.StatusText}".Length > 63
            ? $"Voltix QB Agent — {_loop.StatusText}"[..63]
            : $"Voltix QB Agent — {_loop.StatusText}";
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _logBox.AppendText(line + Environment.NewLine);
                if (_logBox.TextLength > 200_000)
                {
                    _logBox.Text = _logBox.Text[^100_000..];
                }
            });
        }
        catch { /* window going away */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
        {
            // Minimize to tray instead of quitting — the agent is a daemon.
            e.Cancel = true;
            Hide();
            return;
        }
        Log.LineWritten -= OnLogLine;
        _loop?.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}
