using NcrpToConnect.Cli;

namespace NcrpToConnect.PortalReporter;

internal sealed class MainForm : Form
{
    private readonly TextBox _inputPathTextBox = new();
    private readonly TextBox _outputPathTextBox = new();
    private readonly TextBox _previewTextBox = new();
    private readonly Button _selectInputButton = new();
    private readonly Button _selectOutputButton = new();
    private readonly Button _generateButton = new();
    private readonly Label _statusLabel = new();

    public MainForm()
    {
        Text = "Portal PTL Report";
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 5,
            Padding = new Padding(14)
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var inputLabel = new Label { Text = "PTL file", AutoSize = true, Anchor = AnchorStyles.Left };
        var outputLabel = new Label { Text = "Excel report", AutoSize = true, Anchor = AnchorStyles.Left };

        ConfigurePathBox(_inputPathTextBox);
        ConfigurePathBox(_outputPathTextBox);

        _selectInputButton.Text = "Browse...";
        _selectInputButton.Dock = DockStyle.Fill;
        _selectInputButton.Click += (_, _) => SelectInputFile();

        _selectOutputButton.Text = "Save as...";
        _selectOutputButton.Dock = DockStyle.Fill;
        _selectOutputButton.Click += (_, _) => SelectOutputFile();

        _generateButton.Text = "Generate report";
        _generateButton.Width = 150;
        _generateButton.Height = 32;
        _generateButton.Anchor = AnchorStyles.Left;
        _generateButton.Click += (_, _) => GenerateReport();

        _previewTextBox.Dock = DockStyle.Fill;
        _previewTextBox.Multiline = true;
        _previewTextBox.ReadOnly = true;
        _previewTextBox.ScrollBars = ScrollBars.Both;
        _previewTextBox.WordWrap = false;
        _previewTextBox.Font = new Font(FontFamily.GenericMonospace, 9);

        _statusLabel.AutoSize = true;
        _statusLabel.Anchor = AnchorStyles.Left;
        _statusLabel.Text = "Select a .ptl file to begin.";

        root.Controls.Add(inputLabel, 0, 0);
        root.Controls.Add(_inputPathTextBox, 1, 0);
        root.Controls.Add(_selectInputButton, 2, 0);
        root.Controls.Add(outputLabel, 0, 1);
        root.Controls.Add(_outputPathTextBox, 1, 1);
        root.Controls.Add(_selectOutputButton, 2, 1);
        root.Controls.Add(_generateButton, 1, 2);
        root.Controls.Add(_previewTextBox, 0, 3);
        root.Controls.Add(_statusLabel, 0, 4);

        root.SetColumnSpan(_previewTextBox, 3);
        root.SetColumnSpan(_statusLabel, 3);

        Controls.Add(root);
    }

    private static void ConfigurePathBox(TextBox textBox)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.ReadOnly = true;
        textBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }

    private void SelectInputFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Portal export",
            Filter = "Portal export (*.ptl)|*.ptl|Decoded portal JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetDownloadsOrDesktopDirectory(),
            RestoreDirectory = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _inputPathTextBox.Text = dialog.FileName;

        var defaultOutput = Path.Combine(
            Path.GetDirectoryName(dialog.FileName) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{Path.GetFileNameWithoutExtension(dialog.FileName)}-portal-report.xlsx");
        _outputPathTextBox.Text = defaultOutput;
        _statusLabel.Text = "Ready to generate.";
    }

    private void SelectOutputFile()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save Excel report",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_outputPathTextBox.Text)
                ? "portal-report.xlsx"
                : Path.GetFileName(_outputPathTextBox.Text),
            InitialDirectory = string.IsNullOrWhiteSpace(_outputPathTextBox.Text)
                ? GetDownloadsOrDesktopDirectory()
                : Path.GetDirectoryName(_outputPathTextBox.Text),
            RestoreDirectory = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputPathTextBox.Text = dialog.FileName;
        }
    }

    private static string GetDownloadsOrDesktopDirectory()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private void GenerateReport()
    {
        if (string.IsNullOrWhiteSpace(_inputPathTextBox.Text))
        {
            MessageBox.Show(this, "Select a .ptl file first.", "Portal PTL Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputPathTextBox.Text))
        {
            MessageBox.Show(this, "Choose where to save the Excel report.", "Portal PTL Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            UseWaitCursor = true;
            _generateButton.Enabled = false;
            _statusLabel.Text = "Reading portal export...";

            var report = new PortalExportReader().Read(_inputPathTextBox.Text);
            PortalExcelReportWriter.Write(report, _outputPathTextBox.Text);

            _previewTextBox.Text = BuildPreview(report);
            _statusLabel.Text = $"Report saved: {_outputPathTextBox.Text}";

            MessageBox.Show(this, "Excel report generated successfully.", "Portal PTL Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Report generation failed.";
            MessageBox.Show(this, ex.InnerException?.Message ?? ex.Message, "Portal PTL Report", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _generateButton.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static string BuildPreview(PortalExportReport report)
    {
        var lines = new List<string>
        {
            $"Source: {report.SourcePath}",
            $"Pages: {report.PageCount}",
            $"Connectors: {report.ConnectorCount}",
            $"Workflows: {report.WorkflowCount}",
            $"LOVs: {report.LovCount}",
            string.Empty
        };

        foreach (var page in report.Pages)
        {
            lines.Add($"{page.Title} - {page.PortletCount} portlet(s)");
            foreach (var portlet in page.Portlets)
            {
                lines.Add($"  {portlet.PortletType}: {portlet.Title}");
                lines.Add($"    Connectors: {Join(portlet.Connectors)}");
                lines.Add($"    Workflows: {JoinRefs(portlet.Workflows)}");
                lines.Add($"    LOVs: {JoinRefs(portlet.Lovs)}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Join(IEnumerable<string> values)
    {
        var materialized = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
    }

    private static string JoinRefs(IEnumerable<PortalReference> values)
    {
        var materialized = values.Select(v => v.Name).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
    }
}
