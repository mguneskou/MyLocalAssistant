using System.Reflection;

namespace MyLocalAssistant.ServerHost;

internal sealed class ChangelogForm : Form
{
    // ── Changelog ─────────────────────────────────────────────────────────────
    // Keep this in reverse-chronological order (newest first).
    private const string ChangelogText = """
        v2.21.2
        ───────────────────────────────────────────────────────────
        • Fixed: After signing in with an account that requires a password
          change (mustChangePassword=true), the browser stayed on the login
          page instead of navigating to the Change Password screen.
          The RequireGuest routing guard now correctly redirects to
          /change-password in that case.

        v2.21.1
        ───────────────────────────────────────────────────────────
        • Debug: Added browser console logging to the login flow to help
          diagnose sign-in issues. Open DevTools (F12 → Console) before
          logging in to see [MLA] log entries showing the API response,
          routing decisions, and agent loading.

        v2.21.0
        ───────────────────────────────────────────────────────────
        • Excel tools: 9 new tools added — excel.summarize_range (per-column
          stats for large datasets), excel.add_data_validation (dropdown lists,
          numeric/date/text-length rules), excel.add_conditional_format
          (highlight rules, color-scale heat-maps, data bars),
          excel.set_page_setup (orientation, paper, fit-to-page, print area,
          repeat headers), excel.add_named_range, excel.get_named_ranges,
          excel.protect_sheet, excel.unprotect_sheet, excel.copy_range.
        • Excel tools: Fixed excel.read_range — now returns position metadata
          (firstRow, firstColumn) and preserves correct row/column alignment even
          when rows or cells are blank. Booleans and dates are now returned with
          correct types instead of being coerced to numbers.
        • Excel tools: excel.format_range now supports wrapText, verticalAlignment
          (top/center/bottom), insideBorder (thin/medium/thick), and locked (for
          cell-level sheet protection).

        v2.20.2
        ───────────────────────────────────────────────────────────
        • Fixed: The browser client no longer keeps you logged in after closing
          and reopening the browser. Auth tokens and user state are now stored
          in sessionStorage (cleared on browser close) instead of localStorage
          (which persists indefinitely). Refreshing the page within the same
          browser session still keeps you logged in.

        v2.20.1
        ───────────────────────────────────────────────────────────
        • Fixed: Saving the work folder in the browser client settings showed
          "Failed to execute 'json' on 'Response': Unexpected end of JSON input".
          The server correctly returns 204 No Content on success, but the client
          was trying to parse a JSON body that doesn't exist. Fixed.
        • Fixed: After a server restart (or token expiry), the browser client
          showed the chat page but nothing worked — no agents, no conversations
          loaded. Root cause: when the stored refresh token became invalid, the
          client cleared the auth token but kept the user record in localStorage,
          so the app believed the user was still authenticated. Now both the auth
          token and user record are cleared together, and any 401 response from
          the server immediately redirects to the login page.

        v2.20.0
        ───────────────────────────────────────────────────────────
        • Fixed: In the browser client, user messages appeared briefly on the
          left (AI) side of the chat then jumped to the correct right side.
          Root cause: the server returned message roles as "User"/"Assistant"
          (enum names, capitalized) while the client compared against lowercase
          "user"/"assistant". Roles are now normalised to lowercase on load.
        • Fixed: When a local model's context window filled up, the raw C++
          error "llama_decode failed: NoKvSlot" was shown to the user. The
          error is now caught and shown as: "The model's context window is full
          — the conversation is too long for this model. Start a new
          conversation or switch to a model with a larger context window."
        • New: Work folder setting in the browser client. A gear icon next to
          your username (bottom of the sidebar) opens a settings panel where
          you can set a custom work folder path. When set, agent file
          operations land in <your path>\<conversation-id>\ instead of the
          default state\output\<conversation-id>\. Leave blank to use the
          default. The server validates the path (must be absolute, no
          wildcards or '..').

        v2.19.0
        ───────────────────────────────────────────────────────────
        • New: Agents now have a "Scenario notes" field (Admin → Agents →
          Scenario notes button). Free-text rules the admin writes here are
          injected into every prompt right after the tool list, guiding the
          model to use tools consistently and in the correct order.
        • New: Automatic tool-chaining hints. When an agent has both
          client.fs.* and excel.*/word.*/pdf.* tools enabled, the server
          automatically injects a chaining rule into every prompt: the model
          must call client.fs.copyToWorkDir before reading a client file with
          those tools, and client.fs.copyFromWorkDir to return results. No
          manual configuration needed.

        v2.18.0
        ───────────────────────────────────────────────────────────
        • Fixed: Excel and Word files on the client PC can now be processed by
          the agent. Two new tools — client.fs.copyToWorkDir and
          client.fs.copyFromWorkDir — transfer binary files between the client
          shared folder and the server work directory. Tell the agent to "copy
          Deneme.xlsx to the work directory" before asking it to read or edit it.
        • Improved: Three Groq models added — Llama 3.1 8B Instant and Gemma 2 9B
          both offer 14 400 requests/day (highest free limit on Groq), and
          DeepSeek R1 70B distill for reasoning tasks. All use your existing Groq key.
        • Removed: Five Cerebras models (Llama 3.1 70B, Qwen3 32B, Scout 17B,
          GPT-OSS 120B, Z.ai GLM-4.7) and Gemini 1.5 Flash-8B removed from the
          catalog — these were returning errors in testing.

        v2.17.1
        ───────────────────────────────────────────────────────────
        • Fixed: Browser client (http://127.0.0.1:8080) failed to load on
          the portable/installed build because the React SPA files were not
          included in the publish output. The SPA is now correctly bundled
          into the server's wwwroot on every build and publish.

        v2.17.0
        ───────────────────────────────────────────────────────────
        • New: Browser-based chat client replaces the WinForms client window.
          The server now serves a React + TypeScript SPA at http://127.0.0.1:8080.
          "Open Client" in the tray menu opens your default browser — no separate
          app to install or update.
        • Improved: Chat UI redesigned with a professional dark theme (zinc/blue
          palette matching Claude/ChatGPT aesthetics), full Markdown rendering
          with syntax-highlighted code blocks, and streaming token display.
        • Improved: Conversations grouped by date in the sidebar (Today, Yesterday,
          This Week, Older); delete individual conversations inline.
        • New: File attachment support — attach any file via the paperclip button;
          the server extracts text and inlines it into your message automatically.
        • Improved: Messages render tables, lists, bold/italic, and fenced code
          blocks with language-specific highlighting.

        v2.16.2
        ───────────────────────────────────────────────────────────
        • Fixed: Window titles (login, chat, admin) were showing an old
          version number instead of the current release version. Stale
          AssemblyVersion, FileVersion, and InformationalVersion overrides
          have been removed — all version surfaces now derive automatically
          from the single Version property.
        • Improved: All 16 built-in agent system prompts replaced with
          detailed, structured prompts covering proactive tool use, domain
          behaviour guidelines, and hard capability boundaries. Prompts are
          reset to the shipped defaults on every server startup; the global
          admin may override them via the Agents tab after startup.

        v2.16.0
        ───────────────────────────────────────────────────────────
        • New: Excel tool expanded with 8 new functions — rename_sheet,
          copy_sheet, insert_rows, delete_rows, insert_columns, delete_columns,
          sort_range, and find_replace. Agents can now fully manipulate
          worksheet structure and data without leaving the assistant.
        • Improved: Word tool overhauled with 6 functions — word.read now
          returns structured JSON (paragraphs with styles + tables), word.write
          supports inline run formatting (bold, italic, underline, color,
          font size), bullet and numbered lists, tables with borders, and
          horizontal rules. New functions: word.append, word.find_replace,
          word.set_properties, and word.get_properties.
        • New: PowerPoint tool (8 functions) — agents can create .pptx
          presentations from scratch, read slide text, add/write/delete/
          reorder slides, insert tables, and get presentation info.
        • New: PDF tool (4 functions) — pdf.read extracts text per page
          (with optional page range), pdf.merge combines multiple PDFs into
          one, pdf.split separates each page into its own file, and
          pdf.extract_pages copies a page range into a new file.
        • New: PowerPoint files (.pptx) can now be indexed for RAG search
          and read via the client filesystem tool, with one document page
          per slide of extracted text.

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
