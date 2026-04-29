using Microsoft.Extensions.DependencyInjection;
using MyLocalAssistant.Core;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.App.Forms;

internal sealed class FirstRunWizardForm : Form
{
    private readonly ModelCatalogService _catalog;
    private readonly IServiceProvider _services;
    private readonly ListView _list;
    private readonly Label _summary;
    private readonly Button _continue;
    private readonly Button _cancel;

    public FirstRunWizardForm(ModelCatalogService catalog, IServiceProvider services)
    {
        _catalog = catalog;
        _services = services;

        Text = "MyLocalAssistant - First Run Setup";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1000, 700);
        Font = new Font("Segoe UI", 9F);

        var header = new Label
        {
            Text = "Choose the models to download. You can add more later from the Models menu.",
            Dock = DockStyle.Top,
            Padding = new Padding(16, 16, 16, 8),
            AutoSize = false,
            Height = 44,
            Font = new Font("Segoe UI", 10F),
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = false,
            ShowGroups = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9F),
        };
        _list.Columns.Add("Model", 280);
        _list.Columns.Add("Quant", 70);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Min RAM", 80, HorizontalAlignment.Right);
        _list.Columns.Add("License", 160);
        _list.Columns.Add("Description", 360);

        var groups = new Dictionary<ModelTier, ListViewGroup>
        {
            [ModelTier.Lightweight] = new ListViewGroup("Lightweight (CPU-friendly)", HorizontalAlignment.Left),
            [ModelTier.Mid] = new ListViewGroup("Mid (8-16 GB GPU)", HorizontalAlignment.Left),
            [ModelTier.Heavy] = new ListViewGroup("Heavy (24 GB+ GPU)", HorizontalAlignment.Left),
            [ModelTier.Workstation] = new ListViewGroup("Workstation (multi-GPU)", HorizontalAlignment.Left),
        };
        foreach (var g in groups.Values) _list.Groups.Add(g);

        var installedIds = new HashSet<string>(
            _catalog.GetInstalled(Paths.ModelsDirectory).Select(m => m.Catalog.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _catalog.Entries)
        {
            var isInstalled = installedIds.Contains(entry.Id);
            var displayName = isInstalled ? entry.DisplayName + "  (installed)" : entry.DisplayName;
            var item = new ListViewItem(displayName) { Tag = entry, Group = groups[entry.Tier] };
            item.SubItems.Add(entry.Quantization);
            item.SubItems.Add(FormatSize(entry.TotalBytes));
            item.SubItems.Add(entry.MinRamGb > 0 ? $"{entry.MinRamGb} GB" : "—");
            item.SubItems.Add(entry.License);
            item.SubItems.Add(entry.Description);
            if (isInstalled)
            {
                item.ForeColor = SystemColors.GrayText;
                item.Checked = true; // visually shows it's already there; locked below
            }
            _list.Items.Add(item);
        }

        // Lock installed entries: revert any check toggle the user attempts.
        _list.ItemCheck += (_, e) =>
        {
            var entry = (CatalogEntry)_list.Items[e.Index].Tag!;
            if (installedIds.Contains(entry.Id))
            {
                e.NewValue = CheckState.Checked; // always stays checked & ignored downstream
            }
        };
        _list.ItemChecked += (_, _) => UpdateSummary();

        _summary = new Label
        {
            Dock = DockStyle.Bottom,
            Padding = new Padding(16, 8, 16, 0),
            Height = 28,
            Text = "0 models selected, 0 B to download",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 56,
            Padding = new Padding(16, 12, 16, 12),
        };
        _continue = new Button { Text = "Download Selected", Width = 160, Height = 32, Enabled = false };
        _continue.Click += OnContinue;
        _cancel = new Button { Text = "Exit", Width = 100, Height = 32, Margin = new Padding(8, 0, 0, 0) };
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttonPanel.Controls.Add(_continue);
        buttonPanel.Controls.Add(_cancel);

        Controls.Add(_list);
        Controls.Add(_summary);
        Controls.Add(buttonPanel);
        Controls.Add(header);
    }

    private IEnumerable<CatalogEntry> GetSelectedForDownload()
    {
        var installedIds = new HashSet<string>(
            _catalog.GetInstalled(Paths.ModelsDirectory).Select(m => m.Catalog.Id),
            StringComparer.OrdinalIgnoreCase);
        return _list.CheckedItems.Cast<ListViewItem>()
            .Select(i => (CatalogEntry)i.Tag!)
            .Where(e => !installedIds.Contains(e.Id));
    }

    private void UpdateSummary()
    {
        var selected = GetSelectedForDownload().ToList();
        long total = selected.Sum(e => e.TotalBytes);
        _summary.Text = $"{selected.Count} model(s) selected, {FormatSize(total)} to download";
        _continue.Enabled = selected.Count > 0;
    }

    private async void OnContinue(object? sender, EventArgs e)
    {
        var selected = GetSelectedForDownload().ToList();
        if (selected.Count == 0) return;

        Hide();
        using var dlg = new DownloadProgressForm(selected, _services);
        var result = dlg.ShowDialog();
        if (result == DialogResult.OK)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            Show();
        }
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double n = bytes;
        int u = 0;
        while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return $"{n:0.##} {units[u]}";
    }
}
