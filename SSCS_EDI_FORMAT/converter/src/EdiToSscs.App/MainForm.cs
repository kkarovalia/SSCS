using System.Diagnostics;
using System.Reflection;
using EdiToSscs.Core;

namespace EdiToSscs.App;

public sealed class MainForm : Form, IDestinationProvider
{
    private static readonly string[] AllowedExtensions = [".txt", ".edi", ".edi13"];

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
    private readonly ConversionCoordinator _coordinator;
    private readonly Dictionary<QueuedFile, ListViewItem> _rowMap = new();
    private readonly Image? _logoImage;

    private string? _currentFolder;
    private bool _firstShownHandled;

    private Label _folderValueLabel = null!;
    private ListView _fileList = null!;
    private Label _summaryLabel = null!;
    private Panel _dropPanel = null!;

    public MainForm()
    {
        _ = _logger.CleanupOldLogs(DateTime.UtcNow);
        _currentFolder = _settingsStore.Load().OutputFolder;
        _logoImage = LoadEmbeddedLogo();

        var service = new ConversionService(_logger, Application.ProductVersion);
        _coordinator = new ConversionCoordinator(service, this);
        _coordinator.FileChanged += OnFileChanged;
        _coordinator.BatchCompleted += OnBatchCompleted;

        BuildLayout();
        UpdateFolderLabel();

        Shown += OnFormShown;
        FormClosing += OnFormClosingConfirm;
        FormClosed += (_, _) => _logoImage?.Dispose();
    }

    /// <summary>
    /// Marshals an update to the UI thread without ever throwing after the window has
    /// started closing; conversion progress must never depend on the window's lifetime.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            // ObjectDisposedException derives from InvalidOperationException.
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
        if (!_coordinator.IsBusy) return;
        var answer = MessageBox.Show(
            this,
            "Files are still being converted. If you close now, unfinished conversions will stop.\n\nClose anyway?",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            e.Cancel = true;
    }

    public string? CurrentFolder => _currentFolder;

    public DestinationStatus ValidateCurrent() => DestinationValidator.Validate(_currentFolder);

    public string? RequestNewFolder(string reasonMessage)
    {
        if (IsDisposed || !IsHandleCreated)
            return null; // the window is gone; treat as a canceled selection

        if (InvokeRequired)
        {
            // ObjectDisposedException derives from InvalidOperationException.
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
            dialog.SelectedPath = _currentFolder; // harmless if the path no longer exists

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void SaveFolder(string folder)
    {
        _currentFolder = folder;
        try
        {
            _settingsStore.Save(new AppSettings { OutputFolder = folder });
        }
        catch
        {
            RunOnUi(() => MessageBox.Show(
                this,
                "Your folder choice could not be saved and will be asked again next time.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning));
        }

        RunOnUi(UpdateFolderLabel);
    }

    private void BuildLayout()
    {
        Text = "EDI to SSCS Converter";
        MinimumSize = new Size(820, 620);
        Size = new Size(900, 680);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(0);
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

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6),
        };

        var convertPage = new TabPage("Convert")
        {
            BackColor = ThemeBg,
            Padding = new Padding(10),
            UseVisualStyleBackColor = false,
        };
        var settingsPage = new TabPage("Settings")
        {
            BackColor = ThemeBg,
            Padding = new Padding(16),
            UseVisualStyleBackColor = false,
        };

        // Settings is listed first so it appears on the top-left; Convert is selected by default.
        tabs.TabPages.Add(settingsPage);
        tabs.TabPages.Add(convertPage);
        tabs.SelectedTab = convertPage;
        shell.Controls.Add(tabs, 0, 1);

        BuildSettingsPage(settingsPage);
        BuildConvertPage(convertPage);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ThemePanel,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(14, 10, 14, 12),
        };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(ThemeRedAccent, 2f);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        // FlowLayout keeps logo + title fully visible without clipping.
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };

        if (_logoImage is not null)
        {
            var logo = new PictureBox
            {
                Image = _logoImage,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(80, 80),
                Margin = new Padding(0, 0, 14, 0),
                BackColor = Color.Transparent,
            };
            row.Controls.Add(logo);
        }

        var title = new Label
        {
            Text = "EDI to SSCS Converter",
            AutoSize = true,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = ThemeNavy,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 24, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        row.Controls.Add(title);

        header.Controls.Add(row);
        return header;
    }

    private void BuildSettingsPage(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = ThemeBg,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "Output folder",
            AutoSize = true,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = ThemeNavy,
            Margin = new Padding(0, 0, 0, 8),
        };

        var help = new Label
        {
            Text = "Converted SSCS files are saved to this folder.",
            AutoSize = true,
            ForeColor = ThemeMuted,
            Margin = new Padding(0, 0, 0, 12),
        };

        _folderValueLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = ThemeText,
            Margin = new Padding(0, 0, 0, 14),
        };

        var changeFolderButton = MakePrimaryButton("Change Output Folder");
        changeFolderButton.Click += OnChangeOutputFolder;
        changeFolderButton.Margin = new Padding(0);

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(help, 0, 1);
        layout.Controls.Add(_folderValueLabel, 0, 2);
        layout.Controls.Add(changeFolderButton, 0, 3);
        page.Controls.Add(layout);
    }

    private void BuildConvertPage(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = ThemeBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32f));  // drop area
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 48f));  // list
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));  // summary
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // open folder + logs
        page.Controls.Add(root);

        // --- Drop area (click or drag) ---
        _dropPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ThemeNavySoft,
            BorderStyle = BorderStyle.FixedSingle,
            AllowDrop = true,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(16),
        };
        _dropPanel.Paint += OnDropPanelPaint;
        WireDropTarget(_dropPanel);
        _dropPanel.Click += OnChooseFiles;

        var dropInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            AllowDrop = true,
        };
        dropInner.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
        dropInner.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
        WireDropTarget(dropInner);
        dropInner.Click += OnChooseFiles;

        var dropPrimary = new Label
        {
            Text = "Click or drag and drop EDI files here",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomCenter,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = ThemeNavy,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            AllowDrop = true,
        };
        var dropSecondary = new Label
        {
            Text = "Accepted: .txt, .edi, .edi13",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = ThemeMuted,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            AllowDrop = true,
        };
        WireDropTarget(dropPrimary);
        WireDropTarget(dropSecondary);
        dropPrimary.Click += OnChooseFiles;
        dropSecondary.Click += OnChooseFiles;

        dropInner.Controls.Add(dropPrimary, 0, 0);
        dropInner.Controls.Add(dropSecondary, 0, 1);
        _dropPanel.Controls.Add(dropInner);
        root.Controls.Add(_dropPanel, 0, 0);

        // --- File status list ---
        _fileList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            ShowItemToolTips = true,
            HideSelection = false,
            BackColor = ThemePanel,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 8),
        };
        _fileList.Columns.Add("File", 260);
        _fileList.Columns.Add("Status", 90);
        _fileList.Columns.Add("Details", 360);
        _fileList.Resize += (_, _) =>
        {
            int used = 0;
            for (int i = 0; i < _fileList.Columns.Count - 1; i++)
                used += _fileList.Columns[i].Width;
            int remaining = _fileList.ClientSize.Width - used - 4;
            if (remaining > 80)
                _fileList.Columns[_fileList.Columns.Count - 1].Width = remaining;
        };
        root.Controls.Add(_fileList, 0, 1);

        // --- Summary message ---
        _summaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = ThemePanel,
            ForeColor = ThemeText,
            Padding = new Padding(6),
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(_summaryLabel, 0, 2);

        // --- Open Output Folder + View Logs ---
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = ThemeBg,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var openFolderButton = MakePrimaryButton("Open Output Folder");
        openFolderButton.Click += OnOpenOutputFolder;
        openFolderButton.Margin = new Padding(0, 2, 12, 2);
        openFolderButton.Anchor = AnchorStyles.Left;

        var logsLink = new LinkLabel
        {
            Text = "View Logs",
            AutoSize = true,
            LinkColor = ThemeNavy,
            ActiveLinkColor = ThemeRedAccent,
            Margin = new Padding(0, 8, 0, 0),
            Anchor = AnchorStyles.Right,
        };
        logsLink.LinkClicked += OnViewLogs;

        footer.Controls.Add(openFolderButton, 0, 0);
        footer.Controls.Add(logsLink, 1, 0);
        root.Controls.Add(footer, 0, 3);
    }

    private static Button MakePrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = ThemeNavy,
        ForeColor = ThemeButtonText,
        Padding = new Padding(12, 4, 12, 4),
        Cursor = Cursors.Hand,
        FlatAppearance = { BorderSize = 0 },
    };

    private void WireDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDropDragEnter;
        control.DragOver += OnDropDragEnter;
        control.DragDrop += OnDropDragDrop;
    }

    private void OnDropPanelPaint(object? sender, PaintEventArgs e)
    {
        var rect = _dropPanel.ClientRectangle;
        rect.Inflate(-4, -4);
        using var pen = new Pen(ThemeBorder, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        e.Graphics.DrawRectangle(pen, rect);
    }

    private static Image? LoadEmbeddedLogo()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("best-stop-logo.png", StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return null;

        // Clone into a standalone bitmap so the stream can close safely.
        using var temp = Image.FromStream(stream);
        return new Bitmap(temp);
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        if (_firstShownHandled) return;
        _firstShownHandled = true;

        if (DestinationValidator.Validate(_currentFolder) != DestinationStatus.NotConfigured)
            return;

        MessageBox.Show(
            this,
            "Welcome! Please choose the folder where converted SSCS files should be saved.",
            Text,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SaveFolder(dialog.SelectedPath);
    }

    private void OnDropDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = HasAllowedEdiFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDropDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        EnqueuePaths(paths);
    }

    private void OnChooseFiles(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "EDI files (*.txt;*.edi;*.edi13)|*.txt;*.edi;*.edi13",
            Title = "Choose EDI files to convert",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        EnqueuePaths(dialog.FileNames);
    }

    private void EnqueuePaths(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        var allowed = pathList.Where(IsAllowedEdiPath).ToList();
        int rejected = pathList.Count - allowed.Count;

        if (rejected > 0)
        {
            string message = rejected == pathList.Count
                ? "Only EDI files (.txt, .edi, .edi13) can be converted. No matching files were found."
                : $"{rejected} file(s) were skipped because they are not EDI files (.txt, .edi, .edi13).";
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        if (allowed.Count == 0)
            return;

        var added = _coordinator.Enqueue(allowed);
        foreach (var file in added)
            EnsureRow(file);
    }

    private static bool HasAllowedEdiFiles(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
            return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return false;
        return paths.Any(IsAllowedEdiPath);
    }

    private static bool IsAllowedEdiPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
            return false;
        string ext = Path.GetExtension(path);
        return AllowedExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    private void OnChangeOutputFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(_currentFolder))
            dialog.SelectedPath = _currentFolder; // harmless if the path no longer exists

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var status = DestinationValidator.Validate(dialog.SelectedPath);
        if (status != DestinationStatus.Ok)
        {
            MessageBox.Show(
                this,
                DestinationValidator.Describe(status),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SaveFolder(dialog.SelectedPath);
    }

    private void OnOpenOutputFolder(object? sender, EventArgs e)
    {
        // Timed validation so a stale network path can never freeze the window.
        if (DestinationValidator.Validate(_currentFolder) is DestinationStatus.NotConfigured or DestinationStatus.Missing or DestinationStatus.Unreachable or DestinationStatus.NotADirectory)
        {
            MessageBox.Show(
                this,
                "No output folder is set yet, or the folder is no longer available. Please choose an output folder first.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            // Starting the folder path directly (ShellExecute) is space- and Unicode-safe.
            Process.Start(new ProcessStartInfo
            {
                FileName = _currentFolder!,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(
                this,
                "Could not open the output folder in File Explorer.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnViewLogs(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logger.Directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logger.Directory,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(
                this,
                "Could not open the logs folder in File Explorer.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnFileChanged(QueuedFile file)
    {
        void Update()
        {
            var item = EnsureRow(file);
            item.SubItems[1].Text = StatusText(file.Status);
            string detail = FirstLine(file.Detail);
            item.SubItems[2].Text = detail;
            item.ToolTipText = file.Detail;
            item.ForeColor = StatusColor(file.Status);

            if (file.Status is FileStatus.Converted or FileStatus.Warning or FileStatus.Failed or FileStatus.Canceled)
                _summaryLabel.Text = file.Detail;
        }

        RunOnUi(Update);
    }

    private void OnBatchCompleted(string summary)
    {
        RunOnUi(() => _summaryLabel.Text = summary);
    }

    private ListViewItem EnsureRow(QueuedFile file)
    {
        if (_rowMap.TryGetValue(file, out var existing))
            return existing;

        var item = new ListViewItem(file.FileName);
        item.SubItems.Add(StatusText(file.Status));
        item.SubItems.Add(FirstLine(file.Detail));
        item.ToolTipText = file.Detail;
        item.ForeColor = StatusColor(file.Status);
        _fileList.Items.Add(item);
        _rowMap[file] = item;
        return item;
    }

    private void UpdateFolderLabel()
    {
        string display = string.IsNullOrWhiteSpace(_currentFolder) ? "Not set" : _currentFolder;
        _folderValueLabel.Text = "Current folder: " + display;
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
        if (string.IsNullOrEmpty(text))
            return "";
        int idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text : text[..idx];
    }
}
