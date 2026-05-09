using System.Reflection;

namespace MyLocalAssistant.ServerHost;

internal sealed class ChangelogForm : Form
{
    // ── Changelog ─────────────────────────────────────────────────────────────
    // Keep this in reverse-chronological order (newest first).
    private const string ChangelogText = """
        v2.11.0
        ───────────────────────────────────────────────────────────
        • Fixed: After using "Check for updates", the app disappeared from the
          tray and did not reappear until a manual re-launch.
          (The single-instance mutex was still held by the old process when
          Velopack launched the new version, causing the new instance to exit.)

        v2.10.0
        ───────────────────────────────────────────────────────────
        • New: Excel formulas in write_range — cell values starting with '='
          are written as real Excel formulas (e.g. "=SUM(A1:A10)").
        • New: excel.read_formulas tool — inspect formula strings in a range.

        v2.9.0
        ───────────────────────────────────────────────────────────
        • Fixed: Cerebras cloud model returned HTTP 411 on every request.
        • Fixed: Groq "Qwen3 32B" updated to current model ID (qwen/qwen3-32b).
        • Fixed: Groq "Mixtral 8x7B" removed — decommissioned by Groq.
        • Fixed: Gemini 2.5 Flash 404 — updated to stable model ID.
        • Fixed: Attaching .docx files crashed with an OpenXML method error.
        • Fixed: Agents tab default-model dropdown appeared empty even when
          cloud models exist — now lists all cloud models regardless of key config.
        • New: Reading .xlsx/.xls files via the assistant returns readable table
          text instead of raw binary data.

        v2.8.0
        ───────────────────────────────────────────────────────────
        • Multiple bug fixes from tester feedback (Notes 2).

        """;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ChangelogForm()
    {
        var ver = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

        Text            = $"What's New — MyLocalAssistant v{ver}";
        Width           = 640;
        Height          = 520;
        MinimumSize     = new Size(420, 320);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        Icon            = SystemIcons.Information;

        var rtb = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            BackColor   = SystemColors.Window,
            Font        = new Font("Segoe UI", 9.5f),
            BorderStyle = BorderStyle.None,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Text        = ChangelogText,
            Padding     = new Padding(8),
        };
        // Scroll back to top after setting Text.
        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();

        var closeBtn = new Button
        {
            Text         = "Close",
            DialogResult = DialogResult.Cancel,
            Width        = 88,
            Height       = 28,
        };

        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        btnPanel.Controls.Add(closeBtn);
        btnPanel.Resize += (_, _) =>
        {
            closeBtn.Left = btnPanel.Width - closeBtn.Width - 12;
            closeBtn.Top  = (btnPanel.Height - closeBtn.Height) / 2;
        };

        // Add bottom panel before Fill control so Fill gets remaining space.
        Controls.Add(btnPanel);
        Controls.Add(rtb);

        AcceptButton = closeBtn;
        CancelButton = closeBtn;
    }
}
