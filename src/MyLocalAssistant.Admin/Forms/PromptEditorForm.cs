namespace MyLocalAssistant.Admin.Forms;

/// <summary>
/// Modal multiline editor for system-prompt text. Used by the global-admin Agents tab
/// for both per-agent <c>SystemPrompt</c> and the server-wide global system prompt.
/// </summary>
internal sealed class PromptEditorForm : Form
{
    private readonly TextBox _editor;
    private readonly Label _counter;
    private readonly int _maxChars;

    public string PromptText => _editor.Text;

    public PromptEditorForm(string title, string description, string initialText, int maxChars)
    {
        _maxChars = maxChars;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 760;
        Height = 540;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(560, 360);

        var hint = new Label
        {
            Text = description,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 36,
            Padding = new Padding(10, 8, 10, 4),
            ForeColor = SystemColors.GrayText,
        };

        _editor = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            AcceptsTab = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10f),
            MaxLength = maxChars,
            Text = initialText ?? "",
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        _counter = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Left,
            Width = 200,
            ForeColor = SystemColors.GrayText,
        };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90, Dock = DockStyle.Right };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Dock = DockStyle.Right };
        bottom.Controls.Add(_counter);
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);

        Controls.Add(_editor);
        Controls.Add(bottom);
        Controls.Add(hint);

        AcceptButton = ok;
        CancelButton = cancel;

        _editor.TextChanged += (_, _) => UpdateCounter();
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        _counter.Text = $"{_editor.TextLength:N0} / {_maxChars:N0} chars";
    }
}
