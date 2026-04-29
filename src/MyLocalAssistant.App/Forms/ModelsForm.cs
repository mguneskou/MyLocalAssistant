using Microsoft.Extensions.DependencyInjection;
using MyLocalAssistant.Core;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Models;

namespace MyLocalAssistant.App.Forms;

internal sealed class ModelsForm : Form
{
    private readonly IServiceProvider _services;
    private readonly ModelCatalogService _catalog;
    private readonly ListView _installedList;
    private readonly ListView _availableList;
    private readonly Button _deleteBtn;
    private readonly Button _downloadBtn;

    public ModelsForm(IServiceProvider services)
    {
        _services = services;
        _catalog = services.GetRequiredService<ModelCatalogService>();

        Text = "Manage Models";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 550);
        Size = new Size(1000, 650);
        Font = new Font("Segoe UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // Installed tab
        var installedPage = new TabPage("Installed");
        _installedList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
        };
        _installedList.Columns.Add("Model", 280);
        _installedList.Columns.Add("Quant", 80);
        _installedList.Columns.Add("Tier", 110);
        _installedList.Columns.Add("Size on Disk", 120, HorizontalAlignment.Right);
        _installedList.Columns.Add("Path", 360);
        _installedList.SelectedIndexChanged += (_, _) => _deleteBtn!.Enabled = _installedList.SelectedItems.Count > 0;

        var installedButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(8),
        };
        _deleteBtn = new Button { Text = "Delete Selected", Width = 140, Height = 32, Enabled = false };
        _deleteBtn.Click += OnDelete;
        installedButtons.Controls.Add(_deleteBtn);

        installedPage.Controls.Add(_installedList);
        installedPage.Controls.Add(installedButtons);

        // Available tab
        var availablePage = new TabPage("Available");
        _availableList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            ShowGroups = true,
        };
        _availableList.Columns.Add("Model", 280);
        _availableList.Columns.Add("Quant", 70);
        _availableList.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _availableList.Columns.Add("Min RAM", 80, HorizontalAlignment.Right);
        _availableList.Columns.Add("License", 160);
        _availableList.Columns.Add("Description", 320);
        _availableList.ItemChecked += (_, _) => _downloadBtn!.Enabled = _availableList.CheckedItems.Count > 0;

        var availableButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(8),
        };
        _downloadBtn = new Button { Text = "Download Selected", Width = 160, Height = 32, Enabled = false };
        _downloadBtn.Click += OnDownload;
        availableButtons.Controls.Add(_downloadBtn);

        availablePage.Controls.Add(_availableList);
        availablePage.Controls.Add(availableButtons);

        tabs.TabPages.Add(installedPage);
        tabs.TabPages.Add(availablePage);

        var closeBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(8),
        };
        var closeBtn = new Button { Text = "Close", Width = 100, Height = 32 };
        closeBtn.Click += (_, _) => Close();
        closeBar.Controls.Add(closeBtn);

        Controls.Add(tabs);
        Controls.Add(closeBar);

        Load += (_, _) => Refresh();
    }

    private new void Refresh()
    {
        _installedList.Items.Clear();
        _availableList.Items.Clear();
        _availableList.Groups.Clear();

        var installed = _catalog.GetInstalled(Paths.ModelsDirectory);
        var installedIds = installed.Select(i => i.Catalog.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var m in installed)
        {
            var item = new ListViewItem(m.Catalog.DisplayName) { Tag = m };
            item.SubItems.Add(m.Catalog.Quantization);
            item.SubItems.Add(m.Catalog.Tier.ToString());
            item.SubItems.Add(FirstRunWizardForm.FormatSize(m.SizeOnDisk));
            item.SubItems.Add(Path.GetDirectoryName(m.PrimaryFilePath) ?? "");
            _installedList.Items.Add(item);
        }

        var groups = new Dictionary<ModelTier, ListViewGroup>
        {
            [ModelTier.Lightweight] = new("Lightweight (CPU-friendly)", HorizontalAlignment.Left),
            [ModelTier.Mid] = new("Mid (8-16 GB GPU)", HorizontalAlignment.Left),
            [ModelTier.Heavy] = new("Heavy (24 GB+ GPU)", HorizontalAlignment.Left),
            [ModelTier.Workstation] = new("Workstation", HorizontalAlignment.Left),
        };
        foreach (var g in groups.Values) _availableList.Groups.Add(g);

        foreach (var entry in _catalog.Entries.Where(e => !installedIds.Contains(e.Id)))
        {
            var item = new ListViewItem(entry.DisplayName) { Tag = entry, Group = groups[entry.Tier] };
            item.SubItems.Add(entry.Quantization);
            item.SubItems.Add(FirstRunWizardForm.FormatSize(entry.TotalBytes));
            item.SubItems.Add(entry.MinRamGb > 0 ? $"{entry.MinRamGb} GB" : "—");
            item.SubItems.Add(entry.License);
            item.SubItems.Add(entry.Description);
            _availableList.Items.Add(item);
        }

        _deleteBtn.Enabled = false;
        _downloadBtn.Enabled = false;
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        if (_installedList.SelectedItems.Count == 0) return;
        var selected = _installedList.SelectedItems.Cast<ListViewItem>()
            .Select(i => (InstalledModel)i.Tag!).ToList();
        var names = string.Join("\n  • ", selected.Select(s => s.Catalog.DisplayName));
        var ok = MessageBox.Show(this,
            $"Delete the following model(s) from disk?\n\n  • {names}",
            "Delete models",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (ok != DialogResult.Yes) return;

        foreach (var m in selected)
        {
            try
            {
                var dir = Path.GetDirectoryName(m.PrimaryFilePath)!;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to delete {m.Catalog.DisplayName}:\n{ex.Message}", "Delete failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        Refresh();
    }

    private void OnDownload(object? sender, EventArgs e)
    {
        var selected = _availableList.CheckedItems.Cast<ListViewItem>()
            .Select(i => (CatalogEntry)i.Tag!).ToList();
        if (selected.Count == 0) return;

        using var dlg = new DownloadProgressForm(selected, _services);
        dlg.ShowDialog(this);
        Refresh();
    }
}
