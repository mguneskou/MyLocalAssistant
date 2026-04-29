using System.ComponentModel;
using MyLocalAssistant.Admin.Services;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Forms;

internal sealed class CollectionsTab : UserControl
{
    private readonly ServerClient _client;

    private readonly DataGridView _collGrid;
    private readonly BindingList<RagCollectionDto> _collections = new();
    private readonly ToolStripButton _newCollBtn;
    private readonly ToolStripButton _deleteCollBtn;
    private readonly ToolStripButton _refreshBtn;

    private readonly DataGridView _docGrid;
    private readonly BindingList<RagDocumentDto> _documents = new();
    private readonly ToolStripButton _uploadBtn;
    private readonly ToolStripButton _deleteDocBtn;
    private readonly ToolStripLabel _docHeader;

    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;

    public CollectionsTab(ServerClient client)
    {
        _client = client;
        Dock = DockStyle.Fill;

        // ----- Collections (left) -----
        var collToolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _newCollBtn = new ToolStripButton("New collection…");
        _deleteCollBtn = new ToolStripButton("Delete collection") { Enabled = false };
        _refreshBtn = new ToolStripButton("Refresh");
        collToolbar.Items.AddRange(new ToolStripItem[] { _newCollBtn, _deleteCollBtn, new ToolStripSeparator(), _refreshBtn });

        _collGrid = MakeGrid();
        _collGrid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = nameof(RagCollectionDto.Name), Width = 180 },
            new DataGridViewTextBoxColumn { HeaderText = "Docs", DataPropertyName = nameof(RagCollectionDto.DocumentCount), Width = 60 },
            new DataGridViewTextBoxColumn { HeaderText = "Description", DataPropertyName = nameof(RagCollectionDto.Description), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill },
            new DataGridViewTextBoxColumn { HeaderText = "Created", DataPropertyName = nameof(RagCollectionDto.CreatedAt), Width = 150 },
        });
        _collGrid.DataSource = _collections;

        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_collGrid);
        leftPanel.Controls.Add(collToolbar);

        // ----- Documents (right) -----
        var docToolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _uploadBtn = new ToolStripButton("Upload…") { Enabled = false };
        _deleteDocBtn = new ToolStripButton("Delete document") { Enabled = false };
        _docHeader = new ToolStripLabel("  Select a collection to manage its documents.")
        {
            ForeColor = SystemColors.GrayText,
        };
        docToolbar.Items.AddRange(new ToolStripItem[] { _uploadBtn, _deleteDocBtn, new ToolStripSeparator(), _docHeader });

        _docGrid = MakeGrid();
        _docGrid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "File", DataPropertyName = nameof(RagDocumentDto.FileName), Width = 220 },
            new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = nameof(RagDocumentDto.ContentType), Width = 140 },
            new DataGridViewTextBoxColumn { HeaderText = "Size", DataPropertyName = nameof(RagDocumentDto.SizeBytes), Width = 90 },
            new DataGridViewTextBoxColumn { HeaderText = "Chunks", DataPropertyName = nameof(RagDocumentDto.ChunkCount), Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Ingested", DataPropertyName = nameof(RagDocumentDto.IngestedAt), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill },
        });
        _docGrid.DataSource = _documents;

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_docGrid);
        rightPanel.Controls.Add(docToolbar);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 480,
        };
        split.Panel1.Controls.Add(leftPanel);
        split.Panel2.Controls.Add(rightPanel);

        _statusLabel = new ToolStripStatusLabel("");
        _status = new StatusStrip();
        _status.Items.Add(_statusLabel);

        Controls.Add(split);
        Controls.Add(_status);

        _refreshBtn.Click += async (_, _) => await ReloadCollectionsAsync();
        _newCollBtn.Click += async (_, _) => await OnNewCollectionAsync();
        _deleteCollBtn.Click += async (_, _) => await OnDeleteCollectionAsync();
        _uploadBtn.Click += async (_, _) => await OnUploadAsync();
        _deleteDocBtn.Click += async (_, _) => await OnDeleteDocumentAsync();

        _collGrid.SelectionChanged += async (_, _) => await OnCollectionSelectedAsync();
        _docGrid.SelectionChanged += (_, _) => _deleteDocBtn.Enabled = SelectedDocument is not null;

        Load += async (_, _) => await ReloadCollectionsAsync();
    }

    private static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
    };

    private RagCollectionDto? SelectedCollection =>
        _collGrid.CurrentRow?.DataBoundItem as RagCollectionDto;

    private RagDocumentDto? SelectedDocument =>
        _docGrid.CurrentRow?.DataBoundItem as RagDocumentDto;

    private async Task ReloadCollectionsAsync()
    {
        SetBusy(true, "Loading collections…");
        try
        {
            var list = await _client.ListCollectionsAsync();
            _collections.Clear();
            foreach (var c in list) _collections.Add(c);
            _statusLabel.Text = $"{list.Count} collection(s).";
            await OnCollectionSelectedAsync();
        }
        catch (Exception ex) { ShowError("Load failed", ex); }
        finally { SetBusy(false); }
    }

    private async Task OnCollectionSelectedAsync()
    {
        var sel = SelectedCollection;
        _deleteCollBtn.Enabled = sel is not null;
        _uploadBtn.Enabled = sel is not null;
        _deleteDocBtn.Enabled = false;
        _documents.Clear();
        if (sel is null)
        {
            _docHeader.Text = "  Select a collection to manage its documents.";
            return;
        }
        _docHeader.Text = $"  {sel.Name}";
        try
        {
            var docs = await _client.ListDocumentsAsync(sel.Id);
            foreach (var d in docs) _documents.Add(d);
        }
        catch (Exception ex) { ShowError("Load documents failed", ex); }
    }

    private async Task OnNewCollectionAsync()
    {
        using var dlg = new NewCollectionForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true, "Creating collection…");
        try
        {
            var c = await _client.CreateCollectionAsync(dlg.CollectionName, dlg.Description);
            _collections.Add(c);
            _statusLabel.Text = $"Created '{c.Name}'.";
        }
        catch (Exception ex) { ShowError("Create failed", ex); }
        finally { SetBusy(false); }
    }

    private async Task OnDeleteCollectionAsync()
    {
        var sel = SelectedCollection;
        if (sel is null) return;
        var confirm = MessageBox.Show(this,
            $"Delete collection '{sel.Name}' and all its documents?\nThis cannot be undone.",
            "Delete collection", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;
        SetBusy(true, "Deleting…");
        try
        {
            await _client.DeleteCollectionAsync(sel.Id);
            _collections.Remove(sel);
            _statusLabel.Text = $"Deleted '{sel.Name}'.";
        }
        catch (Exception ex) { ShowError("Delete failed", ex); }
        finally { SetBusy(false); }
    }

    private async Task OnUploadAsync()
    {
        var sel = SelectedCollection;
        if (sel is null) return;
        using var ofd = new OpenFileDialog
        {
            Title = "Upload document",
            Filter = "Supported (*.txt;*.md;*.pdf;*.docx;*.html;*.htm)|*.txt;*.md;*.pdf;*.docx;*.html;*.htm|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true, $"Uploading {ofd.FileNames.Length} file(s)…");
        try
        {
            int ok = 0;
            foreach (var path in ofd.FileNames)
            {
                _statusLabel.Text = $"Uploading {Path.GetFileName(path)}…";
                Application.DoEvents();
                var doc = await _client.UploadDocumentAsync(sel.Id, path);
                _documents.Add(doc);
                ok++;
            }
            // Refresh collection counts.
            await ReloadCollectionsAsync();
            _statusLabel.Text = $"Uploaded {ok} file(s).";
        }
        catch (Exception ex) { ShowError("Upload failed", ex); }
        finally { SetBusy(false); }
    }

    private async Task OnDeleteDocumentAsync()
    {
        var coll = SelectedCollection;
        var doc = SelectedDocument;
        if (coll is null || doc is null) return;
        var confirm = MessageBox.Show(this,
            $"Delete document '{doc.FileName}' from '{coll.Name}'?",
            "Delete document", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;
        SetBusy(true, "Deleting…");
        try
        {
            await _client.DeleteDocumentAsync(coll.Id, doc.Id);
            _documents.Remove(doc);
            await ReloadCollectionsAsync();
            _statusLabel.Text = $"Deleted '{doc.FileName}'.";
        }
        catch (Exception ex) { ShowError("Delete failed", ex); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (message is not null) _statusLabel.Text = message;
    }

    private void ShowError(string title, Exception ex)
    {
        _statusLabel.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

internal sealed class NewCollectionForm : Form
{
    private readonly TextBox _name;
    private readonly TextBox _desc;

    public string CollectionName => _name.Text.Trim();
    public string? Description => string.IsNullOrWhiteSpace(_desc.Text) ? null : _desc.Text.Trim();

    public NewCollectionForm()
    {
        Text = "New collection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 180);
        Font = new Font("Segoe UI", 9F);

        var lblName = new Label { Text = "Name:", Left = 12, Top = 16, Width = 80 };
        _name = new TextBox { Left = 100, Top = 12, Width = 300 };
        var lblDesc = new Label { Text = "Description:", Left = 12, Top = 50, Width = 80 };
        _desc = new TextBox { Left = 100, Top = 46, Width = 300, Multiline = true, Height = 70 };
        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK, Left = 224, Top = 132, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 312, Top = 132, Width = 80 };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { lblName, _name, lblDesc, _desc, ok, cancel });

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                MessageBox.Show(this, "Name is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
    }
}
