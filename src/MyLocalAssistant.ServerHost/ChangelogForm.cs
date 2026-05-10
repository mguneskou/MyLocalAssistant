using System.Reflection;

namespace MyLocalAssistant.ServerHost;

internal sealed class ChangelogForm : Form
{
    // ── Changelog ─────────────────────────────────────────────────────────────
    // Keep this in reverse-chronological order (newest first).
    private const string ChangelogText = """
        v2.15.1
        ───────────────────────────────────────────────────────────
        • New: "Deactivate" button in admin Models tab — unloads the active
          model from memory and clears the default, so no model runs until
          the admin explicitly activates one again.

        v2.15.0
        ───────────────────────────────────────────────────────────
        • Improved: Complete chat window redesign — compact top bar with
          icon-only actions, single-card input area with paperclip and send
          button, cleaner chat header showing title and agent selector.
        • Fixed: After a local model fails to load, the Activate button was
          permanently disabled in the admin panel, leaving the machine stuck
          with no way to retry without switching to a different model.
          The button is now re-enabled on failure so admins can retry directly.
        • Improved: Admin panel Models tab now shows "Active (load failed —
          click Activate to retry)" in the Status column for failed active
          models, making the problem and remedy immediately visible.
        • Fixed: On machines with limited available RAM (e.g. 8 GB), a model
          could fail to load even though the weights would fit, because the
          KV-cache for the full 8 192-token context window consumed the
          remaining memory. The loader now automatically retries with a
          reduced context size (4 096 → 2 048) before giving up, so the model
          loads successfully at the cost of a shorter context window.

        v2.14.0
        ───────────────────────────────────────────────────────────
        • Fixed: Input panel was partially hidden on first load (regression
          from v2.13.0 — height was not calculated until first keystroke).
        • Fixed: Chat bubbles clipped the last line of text; selecting down
          revealed hidden words. Padding increased to prevent this.
        • Fixed: Excel files attached via the Attach button now reach the LLM
          more reliably (cleaner document wrapper format; max output tokens
          raised from 512 to 2 048 so analysis responses aren't cut short).
        • New: Word document tool (word.read / word.write) — agents can now
          read and create .docx files in their work directory.
        • New: Word documents (.docx) in the client shared folder are now
          automatically parsed when read via the client filesystem tool, just
          like Excel files. Supply chain agents can read procurement docs.
        • New: Per-response stats appear in the bubble footer after each
          assistant reply: elapsed seconds, token count, and tok/s rate.
        • New: Three new Cerebras cloud models —
            Qwen3 235B A22B (preview), OpenAI GPT-OSS 120B, Z.ai GLM-4.7 (preview).
        • Fixed: Qwen3 8B local model minimum RAM corrected from 8 GB to
          10 GB (8 GB machines should use Qwen3 4B instead).

        v2.13.0
        ───────────────────────────────────────────────────────────
        • Improved: Chat window visual polish — modern look throughout.
        • Improved: User messages now use a subtle tinted bubble instead of
          solid blue, making the transcript easier to read.
        • Improved: Conversation sidebar shows date group headers
          (Today / Yesterday / This Week / This Month / Older).
        • Improved: Each conversation item now shows a coloured avatar circle
          with the first letter of the conversation title.
        • Improved: Message input box uses a smooth rounded border instead
          of the flat OS-default rectangle.
        • Improved: Streaming indicator replaced with a blinking cursor
          (instead of three bouncing dots) while waiting for the first token.
        • Improved: Code blocks show a rounded language pill (e.g. "python")
          in the header bar.
        • Improved: Empty state shows a chat-bubble illustration above the
          "Start a conversation" prompt.
        • Improved: Top bar now shows the app name on the left.
        • Improved: Flat toolbar — ToolStrip replaced with a lightweight Panel
          with regular Buttons; horizontal Send / Attach layout; agent combo
          no longer clips on narrow windows.

        v2.12.0
        ───────────────────────────────────────────────────────────
        • New: "What's New…" item in tray menu shows this changelog.
        • New: "Submit Feedback…" item in tray menu opens a structured form
          testers can fill in and save as a Markdown file to share.

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
