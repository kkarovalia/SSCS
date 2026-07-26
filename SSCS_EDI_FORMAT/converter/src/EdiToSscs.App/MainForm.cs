using System.Diagnostics;
using System.Reflection;
using EdiToSscs.Core;

namespace EdiToSscs.App;

public sealed class MainForm : Form, IDestinationProvider
{
    // Option A — navy primary, light surfaces, red used sparingly for brand accent.
    private static readonly Color ThemeBg = Color.FromArgb(248, 249, 252);
    private static readonly Color ThemePanel = Color.White;
    private static readonly Color ThemeNavy = Color.FromArgb(42, 56, 145);
    private static readonly Color ThemeNavySoft = Color.FromArgb(232, 236, 248);
    private static readonly Color ThemeBorder = Color.FromArgb(120, 135, 185);
    private static readonly Color ThemeMuted = Color.FromArgb(95, 105, 130);
    private static readonly Color ThemeText = Color.FromArgb(35, 42, 65);
    private static readonly Color ThemeRedAccent = Color.FromArgb(209, 40, 47);
    private static readonly Color ThemeButtonText = Color.White;

    private readonly SettingsStore _settingsStore = new();
    private readonly ConversionLogger _logger = new();
    private readonly Image? _logoImage;

    private ConvertTab _ediTab = null!;
    private ConvertTab _pdfTab = null!;

    private string? _currentFolder;
    private bool _firstShownHandled;

    private Label _folderValueLabel = null!;

    public MainForm()
    {
        _ = _logger.CleanupOldLogs(DateTime.UtcNow);
        _currentFolder = _settingsStore.Load().OutputFolder;
        _logoImage = LoadEmbeddedLogo();

        BuildLayout();
        UpdateFolderLabel();

        Shown += OnFormShown;
        FormClosing += OnFormClosingConfirm;
        FormClosed += (_, _) => _logoImage?.Dispose();
    }

    /// <summary>Marshals a UI update without throwing once the window has started closing.</summary>
    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
        else
        {
            action();
        }
    }

    private void OnFormClosingConfirm(object? sender, FormClosingEventArgs e)
    {
        if (!_ediTab.IsBusy && !_pdfTab.IsBusy) return;
        var answer = MessageBox.Show(
            this,
            "Files are still being converted. If you close now, unfinished conversions will stop.\n\nClose anyway?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            e.Cancel = true;
    }

    // ---------------------------------------------------------------- IDestinationProvider

    public string? CurrentFolder => _currentFolder;

    public DestinationStatus ValidateCurrent() => DestinationValidator.Validate(_currentFolder);

    public string? RequestNewFolder(string reasonMessage)
    {
        if (IsDisposed || !IsHandleCreated) return null;
        if (InvokeRequired)
        {
            try { return (string?)Invoke(() => RequestNewFolder(reasonMessage)); }
            catch (InvalidOperationException) { return null; }
        }

        MessageBox.Show(this, reasonMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(_currentFolder))
            dialog.SelectedPath = _currentFolder;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void SaveFolder(string folder)
    {
        _currentFolder = folder;
        try { _settingsStore.SaveOutputFolder(folder); }
        catch
        {
            RunOnUi(() => MessageBox.Show(this,
                "Your folder choice could not be saved and will be asked again next time.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        RunOnUi(UpdateFolderLabel);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        Text = "EDI & PDF to SSCS Converter";
        MinimumSize = new Size(820, 600);
        Size = new Size(940, 700);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeBg;

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ThemeBg,
            Padding = new Padding(12, 10, 12, 12),
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(shell);

        shell.Controls.Add(BuildHeader(), 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        var settingsPage = new TabPage("Settings") { BackColor = ThemeBg, Padding = new Padding(16), UseVisualStyleBackColor = false };
        var ediPage = new TabPage("EDI to SSCS XML") { BackColor = ThemeBg, Padding = new Padding(10), UseVisualStyleBackColor = false };
        var pdfPage = new TabPage("PDF to SSCS XML") { BackColor = ThemeBg, Padding = new Padding(10), UseVisualStyleBackColor = false };

        tabs.TabPages.Add(settingsPage);
        tabs.TabPages.Add(ediPage);
        tabs.TabPages.Add(pdfPage);

        _ediTab = new ConvertTab(this, ConversionKind.Edi,
            "Click or drag and drop EDI files here",
            "Accepted: .txt, .edi, .edi13  —  the data file a vendor emails you");
        _pdfTab = new ConvertTab(this, ConversionKind.Pdf,
            "Click or drag and drop PDF invoices here",
            "Accepted: .pdf (searchable)  —  a vendor's PDF invoice, e.g. Dr Pepper");
        _ediTab.Build(ediPage);
        _pdfTab.Build(pdfPage);

        tabs.SelectedTab = ediPage;
        shell.Controls.Add(tabs, 0, 1);

        BuildSettingsPage(settingsPage);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = ThemePanel, Padding = new Padding(14, 10, 14, 12) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(ThemeRedAccent, 2f);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoSize = false, BackColor = Color.Transparent,
        };

        if (_logoImage is not null)
        {
            row.Controls.Add(new PictureBox
            {
                Image = _logoImage, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(80, 80),
                Margin = new Padding(0, 0, 14, 0), BackColor = Color.Transparent,
            });
        }

        row.Controls.Add(new Label
        {
            Text = "EDI & PDF to SSCS Converter", AutoSize = true,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = ThemeNavy,
            BackColor = Color.Transparent, Margin = new Padding(0, 24, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        });

        header.Controls.Add(row);
        return header;
    }

    // ---------------------------------------------------------------- Settings page

    private void BuildSettingsPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = ThemeBg,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // folder heading
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // folder value
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // change folder button
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // help text / filler
        page.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "Output folder", AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = ThemeNavy, Margin = new Padding(0, 0, 0, 6),
        }, 0, 0);

        _folderValueLabel = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = ThemeText, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_folderValueLabel, 0, 1);

        var changeFolderButton = MakePrimaryButton("Change Output Folder");
        changeFolderButton.Margin = new Padding(0, 0, 0, 16);
        changeFolderButton.Click += OnChangeOutputFolder;
        layout.Controls.Add(changeFolderButton, 0, 2);

        layout.Controls.Add(new Label
        {
            Text = "Converted SSCS files are saved to this folder.\n\n" +
                   "• EDI to SSCS XML — converts the data files vendors email you (.txt, .edi, .edi13).\n" +
                   "• PDF to SSCS XML — converts Dr Pepper's searchable PDF invoices.",
            AutoSize = true, Font = new Font("Segoe UI", 10.5f), ForeColor = ThemeMuted, Margin = new Padding(0, 4, 0, 0),
        }, 0, 3);
    }

    // ---------------------------------------------------------------- shared helpers

    private static Button MakePrimaryButton(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = ThemeNavy, ForeColor = ThemeButtonText, Padding = new Padding(12, 4, 12, 4),
        Cursor = Cursors.Hand, FlatAppearance = { BorderSize = 0 },
    };

    private static Image? LoadEmbeddedLogo()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("best-stop-logo.png", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var temp = Image.FromStream(stream);
        return new Bitmap(temp);
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        ClampToWorkingArea();

        if (_firstShownHandled) return;
        _firstShownHandled = true;

        if (DestinationValidator.Validate(_currentFolder) != DestinationStatus.NotConfigured)
            return;

        MessageBox.Show(this, "Welcome! Please choose the folder where converted SSCS files should be saved.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SaveFolder(dialog.SelectedPath);
    }

    /// <summary>
    /// Keeps the window within the screen's working area so its bottom (the action buttons) is never
    /// pushed off-screen — which can happen at 125–150% display scaling on shorter laptop screens.
    /// </summary>
    private void ClampToWorkingArea()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, wa.Width);
        int h = Math.Min(Height, wa.Height);
        if (w != Width || h != Height)
            Size = new Size(w, h);

        int x = Math.Max(wa.Left, Math.Min(Location.X, wa.Right - Width));
        int y = Math.Max(wa.Top, Math.Min(Location.Y, wa.Bottom - Height));
        if (x != Location.X || y != Location.Y)
            Location = new Point(x, y);
    }

    private void OnChangeOutputFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(_currentFolder))
            dialog.SelectedPath = _currentFolder;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var status = DestinationValidator.Validate(dialog.SelectedPath);
        if (status != DestinationStatus.Ok)
        {
            MessageBox.Show(this, DestinationValidator.Describe(status), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SaveFolder(dialog.SelectedPath);
    }

    private void UpdateFolderLabel()
    {
        _folderValueLabel.Text = "Current folder: " + (string.IsNullOrWhiteSpace(_currentFolder) ? "Not set" : _currentFolder);
    }

    private static string StatusText(FileStatus status) => status switch
    {
        FileStatus.Waiting => "Waiting",
        FileStatus.Converting => "Converting",
        FileStatus.Converted => "Converted",
        FileStatus.Warning => "Warning",
        FileStatus.Failed => "Failed",
        FileStatus.Canceled => "Canceled",
        _ => status.ToString(),
    };

    private static Color StatusColor(FileStatus status) => status switch
    {
        FileStatus.Converted => Color.DarkGreen,
        FileStatus.Warning => Color.DarkOrange,
        FileStatus.Failed => Color.DarkRed,
        FileStatus.Canceled => Color.Gray,
        _ => SystemColors.ControlText,
    };

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text : text[..idx];
    }

    // ---------------------------------------------------------------- per-tab convert UI

    /// <summary>
    /// One convert section (EDI or PDF): a click/drag drop area, a per-file status list and summary.
    /// The PDF section also shows a (Dr Pepper) vendor dropdown; the EDI section needs no vendor. Each
    /// tab owns an independent conversion queue; both share the window's destination folder.
    /// </summary>
    private sealed class ConvertTab
    {
        private readonly MainForm _owner;
        private readonly ConversionKind _kind;
        private readonly string[] _extensions;
        private readonly string _dropPrimaryText;
        private readonly string _dropSecondaryText;
        private readonly ConversionCoordinator _coordinator;
        private readonly Dictionary<QueuedFile, ListViewItem> _rowMap = new();

        private ListView _fileList = null!;
        private Label _summaryLabel = null!;
        private Panel _dropPanel = null!;

        public ConvertTab(MainForm owner, ConversionKind kind, string dropPrimary, string dropSecondary)
        {
            _owner = owner;
            _kind = kind;
            _extensions = kind == ConversionKind.Pdf ? [".pdf"] : [".txt", ".edi", ".edi13"];
            _dropPrimaryText = dropPrimary;
            _dropSecondaryText = dropSecondary;

            var service = new ConversionService(owner._logger, Application.ProductVersion);
            _coordinator = new ConversionCoordinator(service, owner);
            _coordinator.FileChanged += OnFileChanged;
            _coordinator.BatchCompleted += summary => _owner.RunOnUi(() => _summaryLabel.Text = summary);
        }

        public bool IsBusy => _coordinator.IsBusy;

        public void Build(TabPage page)
        {
            // The PDF vendor row (if any) is pinned to the top and the status + action buttons to the
            // bottom (both docked), so a short window — e.g. at 125–150% display scaling — never clips
            // them. Only the drop area and file list in the middle compress. Docked controls are laid
            // out in reverse add order, so the Fill panel is added FIRST and the docked edges after it.
            var vendorRow = _kind == ConversionKind.Pdf ? BuildVendorRow() : null;

            // --- Middle: drop area + file list (fills remaining space, compresses when short) ---
            var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = ThemeBg };
            middle.RowStyles.Add(new RowStyle(SizeType.Percent, 38f)); // drop area
            middle.RowStyles.Add(new RowStyle(SizeType.Percent, 62f)); // list

            _dropPanel = new Panel
            {
                Dock = DockStyle.Fill, BackColor = ThemeNavySoft, BorderStyle = BorderStyle.FixedSingle,
                AllowDrop = true, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(16),
            };
            _dropPanel.Paint += (_, e) =>
            {
                var rect = _dropPanel.ClientRectangle;
                rect.Inflate(-4, -4);
                using var pen = new Pen(ThemeBorder, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                e.Graphics.DrawRectangle(pen, rect);
            };
            WireDropTarget(_dropPanel);
            _dropPanel.Click += OnChooseFiles;

            var dropInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, AllowDrop = true };
            dropInner.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
            dropInner.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
            WireDropTarget(dropInner);
            dropInner.Click += OnChooseFiles;

            var dropPrimary = new Label
            {
                Text = _dropPrimaryText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = ThemeNavy, BackColor = Color.Transparent,
                Cursor = Cursors.Hand, AllowDrop = true,
            };
            var dropSecondary = new Label
            {
                Text = _dropSecondaryText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopCenter,
                ForeColor = ThemeMuted, BackColor = Color.Transparent, Cursor = Cursors.Hand, AllowDrop = true,
            };
            WireDropTarget(dropPrimary);
            WireDropTarget(dropSecondary);
            dropPrimary.Click += OnChooseFiles;
            dropSecondary.Click += OnChooseFiles;
            dropInner.Controls.Add(dropPrimary, 0, 0);
            dropInner.Controls.Add(dropSecondary, 0, 1);
            _dropPanel.Controls.Add(dropInner);
            middle.Controls.Add(_dropPanel, 0, 0);

            _fileList = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                ShowItemToolTips = true, HideSelection = false, BackColor = ThemePanel,
                BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 8),
            };
            _fileList.Columns.Add("File", 260);
            _fileList.Columns.Add("Status", 90);
            _fileList.Columns.Add("Details", 380);
            _fileList.Resize += (_, _) =>
            {
                int used = 0;
                for (int i = 0; i < _fileList.Columns.Count - 1; i++) used += _fileList.Columns[i].Width;
                int remaining = _fileList.ClientSize.Width - used - 4;
                if (remaining > 80) _fileList.Columns[^1].Width = remaining;
            };
            middle.Controls.Add(_fileList, 0, 1);

            // --- Bottom stack: status summary + action buttons (pinned to the bottom) ---
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, ColumnCount = 1, RowCount = 2, BackColor = ThemeBg,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); // summary
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // footer

            _summaryLabel = new Label
            {
                Dock = DockStyle.Fill, AutoSize = false, BorderStyle = BorderStyle.FixedSingle, BackColor = ThemePanel,
                ForeColor = ThemeText, Padding = new Padding(6), TextAlign = ContentAlignment.TopLeft, Margin = new Padding(0, 0, 0, 6),
            };
            bottom.Controls.Add(_summaryLabel, 0, 0);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = ThemeBg,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0),
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var openFolderButton = MakePrimaryButton("Open Output Folder");
            openFolderButton.Margin = new Padding(0, 4, 12, 4);
            openFolderButton.Anchor = AnchorStyles.Left;
            openFolderButton.Click += OnOpenOutputFolder;

            var logsLink = new LinkLabel
            {
                Text = "View Logs", AutoSize = true, LinkColor = ThemeNavy, ActiveLinkColor = ThemeRedAccent,
                Margin = new Padding(0, 10, 2, 0), Anchor = AnchorStyles.Right,
            };
            logsLink.LinkClicked += (_, _) => OpenFolderInExplorer(_owner._logger.Directory, "logs folder", ensureExists: true);

            footer.Controls.Add(openFolderButton, 0, 0);
            footer.Controls.Add(logsLink, 1, 0);
            bottom.Controls.Add(footer, 0, 1);

            // Add the Fill panel first (it takes the leftover center), then the docked edges.
            page.Controls.Add(middle);
            page.Controls.Add(bottom);
            if (vendorRow is not null) page.Controls.Add(vendorRow);
        }

        /// <summary>Builds the PDF section's vendor row: a single-choice "Dr Pepper" dropdown (the only
        /// searchable-PDF vendor today). It is informational — the PDF pipeline always uses Dr Pepper.</summary>
        private static FlowLayoutPanel BuildVendorRow()
        {
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = ThemeBg,
                Padding = new Padding(0, 0, 0, 6),
            };
            row.Controls.Add(new Label
            {
                Text = "Vendor:", AutoSize = true, ForeColor = ThemeText,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(2, 10, 8, 0),
            });
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(0, 6, 0, 0) };
            combo.Items.Add("Dr Pepper");
            combo.SelectedIndex = 0;
            row.Controls.Add(combo);
            row.Controls.Add(new Label
            {
                Text = "  (Dr Pepper searchable PDF invoices)", AutoSize = true, ForeColor = ThemeMuted, Margin = new Padding(4, 10, 0, 0),
            });
            return row;
        }

        private void WireDropTarget(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += OnDropDragEnter;
            control.DragOver += OnDropDragEnter;
            control.DragDrop += OnDropDragDrop;
        }

        private void OnDropDragEnter(object? sender, DragEventArgs e)
        {
            e.Effect = HasAllowedFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDropDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                EnqueuePaths(paths);
        }

        private void OnChooseFiles(object? sender, EventArgs e)
        {
            string filter = _kind == ConversionKind.Pdf
                ? "PDF invoices (*.pdf)|*.pdf"
                : "EDI files (*.txt;*.edi;*.edi13)|*.txt;*.edi;*.edi13";
            using var dialog = new OpenFileDialog { Multiselect = true, Filter = filter, Title = "Choose files to convert", CheckFileExists = true };
            if (dialog.ShowDialog(_owner) == DialogResult.OK)
                EnqueuePaths(dialog.FileNames);
        }

        private void EnqueuePaths(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            var allowed = pathList.Where(IsAllowedPath).ToList();
            int rejected = pathList.Count - allowed.Count;
            if (rejected > 0)
            {
                string accepted = string.Join(", ", _extensions);
                string message = rejected == pathList.Count
                    ? $"Only {accepted} files can be converted here. No matching files were found."
                    : $"{rejected} file(s) were skipped because they are not {accepted} files.";
                MessageBox.Show(_owner, message, _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (allowed.Count == 0) return;

            foreach (var file in _coordinator.Enqueue(allowed, _kind))
                EnsureRow(file);
        }

        private bool HasAllowedFiles(IDataObject? data) =>
            data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(IsAllowedPath);

        private bool IsAllowedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) return false;
            string ext = Path.GetExtension(path);
            return _extensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
        }

        private void OnOpenOutputFolder(object? sender, EventArgs e)
        {
            if (DestinationValidator.Validate(_owner._currentFolder) is DestinationStatus.NotConfigured
                or DestinationStatus.Missing or DestinationStatus.Unreachable or DestinationStatus.NotADirectory)
            {
                MessageBox.Show(_owner,
                    "No output folder is set yet, or the folder is no longer available. Please choose an output folder first.",
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenFolderInExplorer(_owner._currentFolder!, "output folder", ensureExists: false);
        }

        private void OpenFolderInExplorer(string path, string label, bool ensureExists)
        {
            try
            {
                if (ensureExists) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show(_owner, $"Could not open the {label} in File Explorer.",
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnFileChanged(QueuedFile file)
        {
            _owner.RunOnUi(() =>
            {
                var item = EnsureRow(file);
                item.SubItems[1].Text = StatusText(file.Status);
                item.SubItems[2].Text = FirstLine(file.Detail);
                item.ToolTipText = file.Detail;
                item.ForeColor = StatusColor(file.Status);
                if (file.Status is FileStatus.Converted or FileStatus.Warning or FileStatus.Failed or FileStatus.Canceled)
                    _summaryLabel.Text = file.Detail;
            });
        }

        private ListViewItem EnsureRow(QueuedFile file)
        {
            if (_rowMap.TryGetValue(file, out var existing)) return existing;
            var item = new ListViewItem(file.FileName);
            item.SubItems.Add(StatusText(file.Status));
            item.SubItems.Add(FirstLine(file.Detail));
            item.ToolTipText = file.Detail;
            item.ForeColor = StatusColor(file.Status);
            _fileList.Items.Add(item);
            _rowMap[file] = item;
            return item;
        }
    }
}
