using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Admin.UI;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

/// <summary>
/// Owner-only dialog for setting OpenAI / Anthropic API keys and an optional OpenAI base URL.
/// The server never returns the key strings — this dialog only shows whether each provider is
/// configured. Leaving a field blank on save keeps the existing key; choosing "Clear" deletes it.
/// </summary>
internal sealed class CloudKeysDialog : Form
{
    private readonly ServerClient _client;
    private CloudKeysStatusDto _status;

    private readonly Label _openAiState;
    private readonly TextBox _openAiKey;
    private readonly TextBox _openAiBaseUrl;
    private readonly Button _openAiTestBtn;
    private readonly Button _openAiClearBtn;

    private readonly Label _anthropicState;
    private readonly TextBox _anthropicKey;
    private readonly Button _anthropicTestBtn;
    private readonly Button _anthropicClearBtn;

    private readonly Label _groqState;
    private readonly TextBox _groqKey;
    private readonly Button _groqTestBtn;
    private readonly Button _groqClearBtn;

    private readonly Label _geminiState;
    private readonly TextBox _geminiKey;
    private readonly Button _geminiTestBtn;
    private readonly Button _geminiClearBtn;

    private readonly Label _mistralState;
    private readonly TextBox _mistralKey;
    private readonly Button _mistralTestBtn;
    private readonly Button _mistralClearBtn;

    private readonly Label _statusLbl;
    private readonly Button _saveBtn;
    private readonly Button _closeBtn;

    private bool _clearOpenAi;
    private bool _clearAnthropic;
    private bool _clearGroq;
    private bool _clearGemini;
    private bool _clearMistral;

    public CloudKeysDialog(ServerClient client, CloudKeysStatusDto status)
    {
        _client = client;
        _status = status;

        Text = "Cloud LLM keys (global admin)";
        StartPosition = FormStartPosition.CenterParent;
        UiTheme.ApplyDialog(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Width = 720; Height = 820;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 56,
            Padding = new Padding(12, 10, 12, 4),
            ForeColor = SystemColors.GrayText,
            Text = "Conversations sent to a cloud model leave this network and are billed against the configured account. " +
                   "Keys are stored DPAPI-encrypted on the server and are never returned to clients.",
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16, 8, 16, 8),
            AutoSize = false,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- OpenAI ---
        _openAiState = StateLabel(_status.OpenAiConfigured);
        grid.Controls.Add(SectionLabel("OpenAI"), 0, 0);
        grid.Controls.Add(_openAiState, 1, 0);

        _openAiKey = SecretBox();
        _openAiKey.PlaceholderText = _status.OpenAiConfigured ? "(unchanged — leave blank to keep existing key)" : "sk-…";
        grid.Controls.Add(RowLabel("API key"), 0, 1);
        grid.Controls.Add(_openAiKey, 1, 1);

        _openAiBaseUrl = new TextBox
        {
            Dock = DockStyle.Top,
            Text = _status.OpenAiBaseUrl ?? "",
            PlaceholderText = "https://api.openai.com/v1 (override for Azure / proxies)",
        };
        grid.Controls.Add(RowLabel("Base URL (optional)"), 0, 2);
        grid.Controls.Add(_openAiBaseUrl, 1, 2);

        _openAiTestBtn = new Button { Text = "Test", AutoSize = true, Enabled = _status.OpenAiConfigured };
        _openAiClearBtn = new Button { Text = "Clear key", AutoSize = true, Enabled = _status.OpenAiConfigured };
        var openAiBtns = ButtonRow(_openAiTestBtn, _openAiClearBtn);
        grid.Controls.Add(new Label(), 0, 3);
        grid.Controls.Add(openAiBtns, 1, 3);

        // --- Anthropic ---
        _anthropicState = StateLabel(_status.AnthropicConfigured);
        grid.Controls.Add(SectionLabel("Anthropic"), 0, 4);
        grid.Controls.Add(_anthropicState, 1, 4);

        _anthropicKey = SecretBox();
        _anthropicKey.PlaceholderText = _status.AnthropicConfigured ? "(unchanged — leave blank to keep existing key)" : "sk-ant-…";
        grid.Controls.Add(RowLabel("API key"), 0, 5);
        grid.Controls.Add(_anthropicKey, 1, 5);

        _anthropicTestBtn = new Button { Text = "Test", AutoSize = true, Enabled = _status.AnthropicConfigured };
        _anthropicClearBtn = new Button { Text = "Clear key", AutoSize = true, Enabled = _status.AnthropicConfigured };
        var anthBtns = ButtonRow(_anthropicTestBtn, _anthropicClearBtn);
        grid.Controls.Add(new Label(), 0, 6);
        grid.Controls.Add(anthBtns, 1, 6);

        // --- Groq ---
        _groqState = StateLabel(_status.GroqConfigured);
        grid.Controls.Add(SectionLabel("Groq (free)"), 0, 7);
        grid.Controls.Add(_groqState, 1, 7);

        _groqKey = SecretBox();
        _groqKey.PlaceholderText = _status.GroqConfigured ? "(unchanged — leave blank to keep existing key)" : "gsk_…";
        grid.Controls.Add(RowLabel("API key"), 0, 8);
        grid.Controls.Add(_groqKey, 1, 8);

        _groqTestBtn = new Button { Text = "Test", AutoSize = true, Enabled = _status.GroqConfigured };
        _groqClearBtn = new Button { Text = "Clear key", AutoSize = true, Enabled = _status.GroqConfigured };
        var groqBtns = ButtonRow(_groqTestBtn, _groqClearBtn);
        grid.Controls.Add(new Label(), 0, 9);
        grid.Controls.Add(groqBtns, 1, 9);

        // --- Gemini ---
        _geminiState = StateLabel(_status.GeminiConfigured);
        grid.Controls.Add(SectionLabel("Gemini (free)"), 0, 10);
        grid.Controls.Add(_geminiState, 1, 10);

        _geminiKey = SecretBox();
        _geminiKey.PlaceholderText = _status.GeminiConfigured ? "(unchanged — leave blank to keep existing key)" : "AIza…";
        grid.Controls.Add(RowLabel("API key"), 0, 11);
        grid.Controls.Add(_geminiKey, 1, 11);

        _geminiTestBtn = new Button { Text = "Test", AutoSize = true, Enabled = _status.GeminiConfigured };
        _geminiClearBtn = new Button { Text = "Clear key", AutoSize = true, Enabled = _status.GeminiConfigured };
        var geminiBtns = ButtonRow(_geminiTestBtn, _geminiClearBtn);
        grid.Controls.Add(new Label(), 0, 12);
        grid.Controls.Add(geminiBtns, 1, 12);

        // --- Mistral ---
        _mistralState = StateLabel(_status.MistralConfigured);
        grid.Controls.Add(SectionLabel("Mistral (free)"), 0, 13);
        grid.Controls.Add(_mistralState, 1, 13);

        _mistralKey = SecretBox();
        _mistralKey.PlaceholderText = _status.MistralConfigured ? "(unchanged — leave blank to keep existing key)" : "…";
        grid.Controls.Add(RowLabel("API key"), 0, 14);
        grid.Controls.Add(_mistralKey, 1, 14);

        _mistralTestBtn = new Button { Text = "Test", AutoSize = true, Enabled = _status.MistralConfigured };
        _mistralClearBtn = new Button { Text = "Clear key", AutoSize = true, Enabled = _status.MistralConfigured };
        var mistralBtns = ButtonRow(_mistralTestBtn, _mistralClearBtn);
        grid.Controls.Add(new Label(), 0, 15);
        grid.Controls.Add(mistralBtns, 1, 15);

        _statusLbl = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(12, 4, 12, 0), ForeColor = SystemColors.GrayText };
        _saveBtn = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        _closeBtn = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottomBtns = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(12, 8, 12, 8),
        };
        bottomBtns.Controls.Add(_closeBtn);
        bottomBtns.Controls.Add(_saveBtn);
        AcceptButton = _saveBtn;
        CancelButton = _closeBtn;

        Controls.Add(grid);
        Controls.Add(_statusLbl);
        Controls.Add(bottomBtns);
        Controls.Add(hint);

        _saveBtn.Click += async (_, _) => await SaveAsync();
        _openAiTestBtn.Click += async (_, _) => await TestAsync("openai");
        _anthropicTestBtn.Click += async (_, _) => await TestAsync("anthropic");
        _groqTestBtn.Click += async (_, _) => await TestAsync("groq");
        _geminiTestBtn.Click += async (_, _) => await TestAsync("gemini");
        _mistralTestBtn.Click += async (_, _) => await TestAsync("mistral");
        _openAiClearBtn.Click += (_, _) =>
        {
            if (Confirm("Clear the OpenAI API key?")) { _clearOpenAi = true; _openAiKey.Text = ""; _openAiKey.PlaceholderText = "(will be cleared on save)"; }
        };
        _anthropicClearBtn.Click += (_, _) =>
        {
            if (Confirm("Clear the Anthropic API key?")) { _clearAnthropic = true; _anthropicKey.Text = ""; _anthropicKey.PlaceholderText = "(will be cleared on save)"; }
        };
        _groqClearBtn.Click += (_, _) =>
        {
            if (Confirm("Clear the Groq API key?")) { _clearGroq = true; _groqKey.Text = ""; _groqKey.PlaceholderText = "(will be cleared on save)"; }
        };
        _geminiClearBtn.Click += (_, _) =>
        {
            if (Confirm("Clear the Gemini API key?")) { _clearGemini = true; _geminiKey.Text = ""; _geminiKey.PlaceholderText = "(will be cleared on save)"; }
        };
        _mistralClearBtn.Click += (_, _) =>
        {
            if (Confirm("Clear the Mistral API key?")) { _clearMistral = true; _mistralKey.Text = ""; _mistralKey.PlaceholderText = "(will be cleared on save)"; }
        };
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        Font = new Font(UiTheme.BaseFont, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(0, 12, 0, 4),
    };

    private static Label StateLabel(bool configured) => new()
    {
        Text = configured ? "Configured" : "Not configured",
        ForeColor = configured ? UiTheme.Success : UiTheme.Warning,
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 4),
    };

    private static Label RowLabel(string text) => new()
    {
        Text = text + ":",
        AutoSize = true,
        Margin = new Padding(0, 8, 8, 0),
    };

    private static TextBox SecretBox() => new()
    {
        Dock = DockStyle.Top,
        UseSystemPasswordChar = true,
    };

    private static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = Padding.Empty };
        foreach (var b in buttons) p.Controls.Add(b);
        return p;
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "Confirm", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    private async Task SaveAsync()
    {
        try
        {
            _saveBtn.Enabled = false;
            _statusLbl.Text = "Saving\u2026";
            // Encoding: null = leave alone; "" = clear; non-empty = set new value.
            var req = new UpdateCloudKeysRequest(
                OpenAiApiKey: _clearOpenAi ? "" : (string.IsNullOrWhiteSpace(_openAiKey.Text) ? null : _openAiKey.Text),
                AnthropicApiKey: _clearAnthropic ? "" : (string.IsNullOrWhiteSpace(_anthropicKey.Text) ? null : _anthropicKey.Text),
                OpenAiBaseUrl: _openAiBaseUrl.Text ?? "",
                GroqApiKey: _clearGroq ? "" : (string.IsNullOrWhiteSpace(_groqKey.Text) ? null : _groqKey.Text),
                GeminiApiKey: _clearGemini ? "" : (string.IsNullOrWhiteSpace(_geminiKey.Text) ? null : _geminiKey.Text),
                MistralApiKey: _clearMistral ? "" : (string.IsNullOrWhiteSpace(_mistralKey.Text) ? null : _mistralKey.Text));
            _status = await _client.SetCloudKeysAsync(req);
            _clearOpenAi = false;
            _clearAnthropic = false;
            _clearGroq = false;
            _clearGemini = false;
            _clearMistral = false;
            _openAiKey.Text = "";
            _anthropicKey.Text = "";
            _groqKey.Text = "";
            _geminiKey.Text = "";
            _mistralKey.Text = "";
            _openAiState.Text = _status.OpenAiConfigured ? "Configured" : "Not configured";
            _openAiState.ForeColor = _status.OpenAiConfigured ? UiTheme.Success : UiTheme.Warning;
            _anthropicState.Text = _status.AnthropicConfigured ? "Configured" : "Not configured";
            _anthropicState.ForeColor = _status.AnthropicConfigured ? UiTheme.Success : UiTheme.Warning;
            _groqState.Text = _status.GroqConfigured ? "Configured" : "Not configured";
            _groqState.ForeColor = _status.GroqConfigured ? UiTheme.Success : UiTheme.Warning;
            _geminiState.Text = _status.GeminiConfigured ? "Configured" : "Not configured";
            _geminiState.ForeColor = _status.GeminiConfigured ? UiTheme.Success : UiTheme.Warning;
            _mistralState.Text = _status.MistralConfigured ? "Configured" : "Not configured";
            _mistralState.ForeColor = _status.MistralConfigured ? UiTheme.Success : UiTheme.Warning;
            _openAiTestBtn.Enabled = _openAiClearBtn.Enabled = _status.OpenAiConfigured;
            _anthropicTestBtn.Enabled = _anthropicClearBtn.Enabled = _status.AnthropicConfigured;
            _groqTestBtn.Enabled = _groqClearBtn.Enabled = _status.GroqConfigured;
            _geminiTestBtn.Enabled = _geminiClearBtn.Enabled = _status.GeminiConfigured;
            _mistralTestBtn.Enabled = _mistralClearBtn.Enabled = _status.MistralConfigured;
            _openAiKey.PlaceholderText = _status.OpenAiConfigured ? "(unchanged — leave blank to keep existing key)" : "sk-…";
            _anthropicKey.PlaceholderText = _status.AnthropicConfigured ? "(unchanged — leave blank to keep existing key)" : "sk-ant-…";
            _groqKey.PlaceholderText = _status.GroqConfigured ? "(unchanged — leave blank to keep existing key)" : "gsk_…";
            _geminiKey.PlaceholderText = _status.GeminiConfigured ? "(unchanged — leave blank to keep existing key)" : "AIza…";
            _mistralKey.PlaceholderText = _status.MistralConfigured ? "(unchanged — leave blank to keep existing key)" : "…";
            _statusLbl.Text = "Saved.";
        }
        catch (Exception ex)
        {
            _statusLbl.Text = "Save failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _saveBtn.Enabled = true; }
    }

    private async Task TestAsync(string provider)
    {
        try
        {
            _statusLbl.Text = $"Testing {provider}\u2026";
            var r = await _client.TestCloudKeyAsync(provider);
            _statusLbl.Text = $"{provider}: {(r.Ok ? "OK" : "FAIL")} — {r.Detail}";
        }
        catch (Exception ex) { _statusLbl.Text = $"{provider}: error — {ex.Message}"; }
    }
}
