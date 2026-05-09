using System.Reflection;
using System.Text;

namespace MyLocalAssistant.ServerHost;

/// <summary>
/// Structured feedback form for testers. Saves output as a Markdown file
/// (easy for the developer/AI to read when shared back).
/// </summary>
internal sealed class FeedbackForm : Form
{
    // ── Field controls ────────────────────────────────────────────────────────

    private readonly TextBox     _nameBox;
    private readonly ComboBox    _areaBox;
    private readonly ComboBox    _typeBox;
    private readonly TextBox     _summaryBox;
    private readonly RichTextBox _descriptionBox;
    private readonly RichTextBox _stepsBox;
    private readonly RichTextBox _expectedBox;
    private readonly RichTextBox _actualBox;
    private readonly RichTextBox _notesBox;
    private readonly string      _version;

    // ── Dropdown options ──────────────────────────────────────────────────────

    private static readonly string[] Areas =
    {
        "Chat", "Agents", "Excel Tool", "Cloud Models", "Local Models",
        "File Attachments", "Web Search", "Admin Panel",
        "Installation / Updates", "Other",
    };

    private static readonly string[] Types =
    {
        "Bug Report", "Feature Request", "Improvement", "General Comment",
    };

    // ── Layout constants ──────────────────────────────────────────────────────

    // Row heights in the TableLayoutPanel (must match RowStyles below).
    private static readonly int[] RowHeights = { 28, 22, 28, 28, 28, 96, 96, 76, 76, 76 };
    private const int LabelColumnWidth = 158;
    private const int TlpPaddingV      = 10;

    // ── Constructor ───────────────────────────────────────────────────────────

    public FeedbackForm()
    {
        _version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

        Text            = "Submit Feedback — MyLocalAssistant";
        Width           = 680;
        Height          = 660;
        MinimumSize     = new Size(520, 500);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        Icon            = SystemIcons.Question;

        // ── Build TableLayoutPanel ──────────────────────────────────────────

        int tlpHeight = RowHeights.Sum() + TlpPaddingV * 2 + RowHeights.Length * 4;

        var tlp = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount    = RowHeights.Length,
            Height      = tlpHeight,
            Padding     = new Padding(12, TlpPaddingV, 12, TlpPaddingV),
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));
        foreach (var h in RowHeights)
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, h + 4));

        int row = 0;

        // Tester name
        AddLabel(tlp, "Your name:", row);
        _nameBox = new TextBox { Dock = DockStyle.Fill };
        tlp.Controls.Add(_nameBox, 1, row++);

        // Version (read-only)
        AddLabel(tlp, "Version tested:", row);
        tlp.Controls.Add(new Label
        {
            Text      = _version,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
        }, 1, row++);

        // Area
        AddLabel(tlp, "Area:", row);
        _areaBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _areaBox.Items.AddRange(Areas);
        _areaBox.SelectedIndex = 0;
        tlp.Controls.Add(_areaBox, 1, row++);

        // Feedback type
        AddLabel(tlp, "Feedback type:", row);
        _typeBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _typeBox.Items.AddRange(Types);
        _typeBox.SelectedIndex = 0;
        tlp.Controls.Add(_typeBox, 1, row++);

        // Summary (required)
        AddLabel(tlp, "Summary *:", row);
        _summaryBox = new TextBox { Dock = DockStyle.Fill };
        tlp.Controls.Add(_summaryBox, 1, row++);

        // Description
        AddLabel(tlp, "Description:", row);
        _descriptionBox = MakeRichBox();
        tlp.Controls.Add(_descriptionBox, 1, row++);

        // Steps to reproduce
        AddLabel(tlp, "Steps to reproduce\n(if bug):", row);
        _stepsBox = MakeRichBox();
        tlp.Controls.Add(_stepsBox, 1, row++);

        // Expected / Actual
        AddLabel(tlp, "Expected behavior:", row);
        _expectedBox = MakeRichBox();
        tlp.Controls.Add(_expectedBox, 1, row++);

        AddLabel(tlp, "Actual behavior:", row);
        _actualBox = MakeRichBox();
        tlp.Controls.Add(_actualBox, 1, row++);

        // Additional notes
        AddLabel(tlp, "Additional notes:", row);
        _notesBox = MakeRichBox();
        tlp.Controls.Add(_notesBox, 1, row++);

        // ── Scroll panel ────────────────────────────────────────────────────

        var scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scrollPanel.Controls.Add(tlp);
        // Keep TLP width in sync with the scrollable panel.
        scrollPanel.Resize += (_, _) =>
            tlp.Width = Math.Max(1, scrollPanel.ClientSize.Width
                - (scrollPanel.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));

        // ── Button bar ──────────────────────────────────────────────────────

        var saveBtn = new Button
        {
            Text   = "Save Feedback…",
            Height = 28,
            Width  = 130,
        };
        var cancelBtn = new Button
        {
            Text         = "Cancel",
            Height       = 28,
            Width        = 88,
            DialogResult = DialogResult.Cancel,
        };
        saveBtn.Click += OnSave;

        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(saveBtn);
        btnPanel.Resize += (_, _) =>
        {
            int top          = (btnPanel.Height - saveBtn.Height) / 2;
            cancelBtn.Left   = btnPanel.Width - cancelBtn.Width - 12;
            cancelBtn.Top    = top;
            saveBtn.Left     = cancelBtn.Left - saveBtn.Width - 8;
            saveBtn.Top      = top;
        };

        // ── Compose form ────────────────────────────────────────────────────

        Controls.Add(scrollPanel);   // Fill (must be added after Bottom controls)
        Controls.Add(btnPanel);      // Bottom

        CancelButton = cancelBtn;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddLabel(TableLayoutPanel tlp, string text, int row)
    {
        tlp.Controls.Add(new Label
        {
            Text      = text,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.TopRight,
            Padding   = new Padding(0, 5, 8, 0),
        }, 0, row);
    }

    private static RichTextBox MakeRichBox() => new()
    {
        Dock        = DockStyle.Fill,
        ScrollBars  = RichTextBoxScrollBars.Vertical,
        Font        = new Font("Segoe UI", 9f),
        AcceptsTab  = false,
    };

    // ── Save ──────────────────────────────────────────────────────────────────

    private void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_summaryBox.Text))
        {
            MessageBox.Show("Please enter a summary before saving.",
                "Feedback", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _summaryBox.Focus();
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title       = "Save Feedback File",
            Filter      = "Markdown file (*.md)|*.md|Text file (*.txt)|*.txt",
            FileName    = $"feedback_{DateTime.Now:yyyyMMdd_HHmm}.md",
            DefaultExt  = "md",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        File.WriteAllText(dlg.FileName, BuildMarkdown(), Encoding.UTF8);

        MessageBox.Show($"Feedback saved.\n\nPlease share this file with your contact:\n{dlg.FileName}",
            "Feedback Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);

        DialogResult = DialogResult.OK;
        Close();
    }

    private string BuildMarkdown()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# MyLocalAssistant — Tester Feedback");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| **Tester** | {Esc(_nameBox.Text.Trim().IfEmpty("(not provided)"))} |");
        sb.AppendLine($"| **Date** | {DateTime.Now:yyyy-MM-dd} |");
        sb.AppendLine($"| **Version** | {_version} |");
        sb.AppendLine($"| **Area** | {_areaBox.Text} |");
        sb.AppendLine($"| **Type** | {_typeBox.Text} |");
        sb.AppendLine();

        Section(sb, "Summary",              _summaryBox.Text,     required: true);
        Section(sb, "Description",          _descriptionBox.Text);
        Section(sb, "Steps to Reproduce",   _stepsBox.Text);
        Section(sb, "Expected Behavior",    _expectedBox.Text);
        Section(sb, "Actual Behavior",      _actualBox.Text);
        Section(sb, "Additional Notes",     _notesBox.Text);

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string heading, string text, bool required = false)
    {
        var trimmed = text?.Trim() ?? "";
        if (!required && string.IsNullOrWhiteSpace(trimmed)) return;
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(trimmed) ? "_(not provided)_" : trimmed);
        sb.AppendLine();
    }

    /// <summary>Escape pipe characters so the Markdown table renders correctly.</summary>
    private static string Esc(string s) => s.Replace("|", "\\|");
}

internal static class StringExtensions
{
    internal static string IfEmpty(this string s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s;
}
