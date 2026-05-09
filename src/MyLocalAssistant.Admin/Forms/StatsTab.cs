using MyLocalAssistant.Admin.Services;

namespace MyLocalAssistant.Admin.Forms;

/// <summary>
/// Usage dashboard tab. Shows aggregate chat counts, error rate,
/// per-agent breakdown, and a 30-day trend sparkline.
/// </summary>
internal sealed class StatsTab : UserControl
{
    private readonly ServerClient _client;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _refreshBtn;
    private readonly ToolStripLabel _rangeLabel;
    private readonly ToolStripComboBox _rangeCombo;
    private readonly Panel _summaryPanel;
    private readonly Label _totalLabel;
    private readonly Label _usersLabel;
    private readonly Label _errorLabel;
    private readonly DataGridView _agentGrid;
    private readonly SparklinePanel _sparkline;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;

    public StatsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _refreshBtn = new ToolStripButton("Refresh");
        _rangeLabel = new ToolStripLabel("  Window: ");
        _rangeCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _rangeCombo.Items.AddRange(new object[] { "7 days", "30 days", "90 days" });
        _rangeCombo.SelectedIndex = 1;
        _toolbar.Items.AddRange(new ToolStripItem[] { _refreshBtn, _rangeLabel, _rangeCombo });

        _summaryPanel = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 6, 8, 6) };
        _totalLabel = MakeSummaryLabel();
        _usersLabel = MakeSummaryLabel();
        _errorLabel = MakeSummaryLabel();
        var summaryFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        summaryFlow.Controls.AddRange(new Control[] { _totalLabel, _usersLabel, _errorLabel });
        _summaryPanel.Controls.Add(summaryFlow);

        _agentGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };
        _agentGrid.RowTemplate.Height = 28;
        _agentGrid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Agent", DataPropertyName = "AgentId", Width = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "Chats", DataPropertyName = "Count", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Errors", DataPropertyName = "Errors", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Error %", DataPropertyName = "ErrorPct", Width = 80 });

        _sparkline = new SparklinePanel { Dock = DockStyle.Bottom, Height = 80 };

        _status = new StatusStrip { SizingGrip = false };
        _statusLabel = new ToolStripStatusLabel("Not loaded.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _status.Items.Add(_statusLabel);

        // Layout: toolbar → summary → grid fills → sparkline → statusbar
        Controls.Add(_agentGrid);
        Controls.Add(_sparkline);
        Controls.Add(_summaryPanel);
        Controls.Add(_toolbar);
        Controls.Add(_status);

        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        _rangeCombo.SelectedIndexChanged += async (_, _) => await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        _refreshBtn.Enabled = false;
        _statusLabel.Text = "Loading\u2026";
        try
        {
            var days = _rangeCombo.SelectedIndex switch { 0 => 7, 2 => 90, _ => 30 };
            var stats = await _client.GetStatsAsync(days);

            _totalLabel.Text = $"Chats: {stats.TotalChats:N0}";
            _usersLabel.Text = $"Active users: {stats.ActiveUsers:N0}";
            _errorLabel.Text = $"Error rate: {stats.ErrorRate:P1}";

            var rows = stats.ByAgent.Select(a => new
            {
                a.AgentId,
                a.Count,
                a.Errors,
                ErrorPct = a.Count == 0 ? "–" : $"{(double)a.Errors / a.Count:P1}",
            }).ToList();

            _agentGrid.DataSource = rows;
            _sparkline.SetData(stats.DailyChats.Select(d => (double)d.Count).ToArray(),
                stats.DailyChats.Select(d => d.Day.ToString("MMM d")).ToArray());

            _statusLabel.Text = $"Last refreshed {DateTime.Now:HH:mm:ss} · {days}-day window";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Error: " + ex.Message;
        }
        finally
        {
            _refreshBtn.Enabled = true;
        }
    }

    private static Label MakeSummaryLabel() => new()
    {
        AutoSize = false,
        Width = 180,
        Height = 36,
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 16, 0),
    };

    // ─── Simple sparkline chart ────────────────────────────────────────────────
    private sealed class SparklinePanel : Panel
    {
        private double[] _values = Array.Empty<double>();
        private string[] _labels = Array.Empty<string>();

        public SparklinePanel() { DoubleBuffered = true; }

        public void SetData(double[] values, string[] labels)
        {
            _values = values;
            _labels = labels;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_values.Length == 0) return;
            var g = e.Graphics;
            g.Clear(SystemColors.Window);

            var pad = new Padding(40, 8, 8, 20);
            var w = Width - pad.Left - pad.Right;
            var h = Height - pad.Top - pad.Bottom;
            if (w <= 0 || h <= 0) return;

            var max = _values.Max();
            if (max <= 0) max = 1;
            var barW = Math.Max(1, w / _values.Length - 2);

            using var barBrush = new SolidBrush(Color.FromArgb(0, 120, 215));
            using var labelFont = new Font("Segoe UI", 7.5f);
            using var axisFont = new Font("Segoe UI", 7.5f);
            using var axisPen = new Pen(SystemColors.ControlLight);

            g.DrawLine(axisPen, pad.Left, pad.Top + h, pad.Left + w, pad.Top + h);

            for (var i = 0; i < _values.Length; i++)
            {
                var x = pad.Left + i * (w / _values.Length);
                var barH = (int)(_values[i] / max * h);
                if (barH > 0)
                    g.FillRectangle(barBrush, x + 1, pad.Top + h - barH, barW, barH);

                // Label every ~7 bars
                if (i % 7 == 0 && i < _labels.Length)
                    g.DrawString(_labels[i], labelFont, SystemBrushes.GrayText,
                        new PointF(x, pad.Top + h + 2));
            }

            // Y-axis max label
            g.DrawString(max.ToString("N0"), axisFont, SystemBrushes.GrayText, new PointF(2, pad.Top));
        }
    }
}
