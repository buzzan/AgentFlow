using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal sealed class SchedulerForm : Form
{
    private static readonly Color Accent = Color.FromArgb(29, 78, 216);
    private static readonly Color SecondaryText = SystemInformation.HighContrast
        ? SystemColors.WindowText : Color.FromArgb(89, 89, 89);
    private readonly TextBox project = new TextBox();
    private readonly TextBox claude = new TextBox();
    private readonly ComboBox agent = new ComboBox();
    private readonly DailyTimePicker time = new DailyTimePicker();
    private readonly TextBox prompt = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly ScheduleList jobs = new ScheduleList();
    private readonly Label selection = new Label { AutoSize = true };
    private readonly Label registrationStatus = new Label { AutoSize = true, ForeColor = SecondaryText };
    private readonly Button openLogButton;
    private readonly Button cancelButton;

    internal SchedulerForm()
    {
        Text = "AgentFlow";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.Font;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(240, 244, 249);
        ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(32, 32, 32);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 680);
        MinimumSize = new Size(800, 670);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var registration = new Section(true, 2);
        registration.Margin = new Padding(0, 0, 0, 12);
        registration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        registration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.Controls.Add(registration, 0, 0);
        var registrationHeading = Heading("New Schedule", 16);
        registration.Controls.Add(registrationHeading, 0, 0);
        registration.SetColumnSpan(registrationHeading, 2);
        var browseProject = Button("Browse Folder", BrowseProject);
        browseProject.Anchor = AnchorStyles.Left;
        var projectField = Field("Project", project, browseProject);
        projectField.Dock = DockStyle.None;
        projectField.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var projectMargin = projectField.Margin;
        projectMargin.Top += 5;
        projectField.Margin = projectMargin;
        registration.Controls.Add(projectField, 0, 1);
        registration.Controls.Add(Field("Claude CLI", claude, Button("Browse File", BrowseClaude)), 0, 2);
        var reloadAgents = Button("Reload Agents", LoadAgents);
        reloadAgents.Anchor = AnchorStyles.Left;
        registration.Controls.Add(Field("Agent", agent, reloadAgents), 0, 3);
        agent.DropDownStyle = ComboBoxStyle.DropDown;
        project.Leave += delegate { Safe(LoadAgents); };
        claude.Text = FindClaude();

        DateTime initialTime = DateTime.Now.AddMinutes(5);
        time.Time = initialTime.TimeOfDay;
        var schedule = Field("Daily Run Time", time, showCaption: false);
        schedule.Controls.Add(new Label { Text = "Every day · Windows local time", AutoSize = true, ForeColor = SecondaryText }, 0, 2);
        registration.Controls.Add(schedule, 1, 1);
        prompt.Height = 85;
        var instruction = Field("Task Instructions", prompt);
        registration.Controls.Add(instruction, 1, 2);
        registration.SetRowSpan(instruction, 2);
        instruction.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        registrationStatus.Text = "Runs when you are signed in to Windows, even if the app is closed.";
        registrationStatus.AccessibleName = "Scheduling Information";
        var registrationFooter = Bar(
            registrationStatus,
            Button("Schedule", Register, true));
        registrationFooter.Margin = new Padding(0, 8, 0, 0);
        registration.Controls.Add(registrationFooter, 0, 4);
        registration.SetColumnSpan(registrationFooter, 2);

        var history = new Section(false, 1);
        history.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        history.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        history.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        history.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(history, 0, 1);
        history.Controls.Add(Bar(Heading("Scheduled Runs", 16), Button("Refresh", RefreshJobs)), 0, 0);

        jobs.AccessibleName = "Scheduled Runs";
        jobs.Dock = DockStyle.Fill;
        jobs.Margin = new Padding(0, 6, 0, 6);
        jobs.SelectionChanged += UpdateSelection;
        jobs.OpenLogRequested += delegate { Safe(OpenLog); };
        jobs.ToggleRequested += ToggleSchedule;
        history.Controls.Add(jobs, 0, 1);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        openLogButton = Button("Open Log", OpenLog);
        cancelButton = Button("Cancel Schedule", Cancel);
        buttons.Controls.Add(openLogButton);
        buttons.Controls.Add(cancelButton);
        selection.ForeColor = SecondaryText;
        UpdateSelection();
        history.Controls.Add(Bar(selection, buttons), 0, 2);
        Shown += delegate { Safe(RefreshJobs); };
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        if (IsHandleCreated && WindowState != FormWindowState.Minimized)
            Invalidate(true);
    }

    protected override void OnResizeEnd(EventArgs args)
    {
        base.OnResizeEnd(args);
        Invalidate(true);
    }

    private Label Heading(string text, float size)
    {
        var font = new Font(Font.FontFamily, size, FontStyle.Bold);
        var label = new Label { Text = text, AutoSize = true, Font = font };
        label.Disposed += delegate { font.Dispose(); };
        return label;
    }

    private sealed class Section : TableLayoutPanel
    {
        internal Section(bool autoSize, int columns)
        {
            AutoSize = autoSize;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            ColumnCount = columns;
            Padding = new Padding(16);
            Margin = Padding.Empty;
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            float diameter = 24 * e.Graphics.DpiX / 96;
            float right = ClientSize.Width - 1;
            float bottom = ClientSize.Height - 1;
            if (right <= diameter || bottom <= diameter) return;

            using (var shape = new GraphicsPath())
            using (var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.White))
            using (var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(224, 224, 224)))
            {
                shape.AddArc(0, 0, diameter, diameter, 180, 90);
                shape.AddArc(right - diameter, 0, diameter, diameter, 270, 90);
                shape.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
                shape.AddArc(0, bottom - diameter, diameter, diameter, 90, 90);
                shape.CloseFigure();
                var state = e.Graphics.Save();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(fill, shape);
                e.Graphics.DrawPath(border, shape);
                e.Graphics.Restore(state);
            }
        }
    }

    private static TableLayoutPanel Bar(Control left, Control right)
    {
        var bar = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.Anchor = AnchorStyles.Left;
        right.Anchor = AnchorStyles.Right;
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 1, 0);
        return bar;
    }

    private static TableLayoutPanel Field(string label, Control field, Button button = null, bool showCaption = true)
    {
        var group = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 3, 8, 3) };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (showCaption)
        {
            var caption = new Label { Text = label, AutoSize = true };
            group.Controls.Add(caption, 0, 0);
            group.SetColumnSpan(caption, 2);
        }
        field.AccessibleName = label;
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        group.Controls.Add(field, 0, 1);
        if (button == null) group.SetColumnSpan(field, 2);
        else group.Controls.Add(button, 1, 1);
        return group;
    }

    private Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(8, 0, 8, 0) };
        if (primary && !SystemInformation.HighContrast)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Accent;
        }
        button.Click += delegate { Safe(action); };
        return button;
    }

    private void UpdateSelection()
    {
        var row = jobs.Selected;
        openLogButton.Enabled = row != null;
        cancelButton.Enabled = row != null;
        selection.Text = row == null ? "No schedule selected" : row.Job.Agent + " · " + row.Status;
    }

    private void ToggleSchedule(ScheduleList.Row row, bool enabled)
    {
        Safe(delegate
        {
            var scheduler = new WindowsScheduler();
            scheduler.SetEnabled(row.Job, enabled);
            row.Enabled = enabled;
            row.Status = scheduler.Status(row.Job, out row.Enabled);
        });
        UpdateSelection();
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static string FindClaude()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
        return new[] { Path.Combine(profile, ".local", "bin", "claude.exe") }
            .Concat(folders.Select(folder => folder.Trim().Trim('"'))
                .Where(Path.IsPathRooted).Select(folder => Path.Combine(folder, "claude.exe")))
            .FirstOrDefault(File.Exists) ?? "";
    }

    private void BrowseClaude()
    {
        using (var dialog = new OpenFileDialog { Filter = "Claude Code (claude.exe)|claude.exe" })
            if (dialog.ShowDialog(this) == DialogResult.OK) claude.Text = dialog.FileName;
    }

    private void BrowseProject()
    {
        using (var dialog = new FolderBrowserDialog { SelectedPath = project.Text, ShowNewFolderButton = false })
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                project.Text = dialog.SelectedPath;
                LoadAgents();
            }
    }

    private void LoadAgents()
    {
        string selected = agent.Text;
        agent.Items.Clear();
        if (Directory.Exists(project.Text))
        {
            string folder = Path.Combine(project.Text, ".claude", "agents");
            if (Directory.Exists(folder))
                foreach (string path in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories))
                    using (var file = File.OpenText(path))
                    {
                        if ((file.ReadLine() ?? "").Trim() != "---") continue;
                        string line;
                        string name = "";
                        while ((line = file.ReadLine()) != null && line.Trim() != "---")
                        {
                            var match = Regex.Match(line, @"^name:\s*(.+)$");
                            if (match.Success) name = match.Groups[1].Value.Trim().Trim('"', '\'');
                        }
                        if (line != null && Job.ValidAgent(name) && !agent.Items.Contains(name)) agent.Items.Add(name);
                    }
        }
        agent.Text = selected;
    }

    private void Register()
    {
        if (!Directory.Exists(project.Text)) throw new ArgumentException("Select a project folder.");
        if (!File.Exists(claude.Text) || !String.Equals(Path.GetFileName(claude.Text), "claude.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the Claude Code claude.exe file.");
        if (!Job.ValidAgent(agent.Text.Trim())) throw new ArgumentException("Enter a valid agent name.");
        if (String.IsNullOrWhiteSpace(prompt.Text)) throw new ArgumentException("Enter task instructions.");
        TimeSpan runTime = time.Time;
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"),
            Project = Path.GetFullPath(project.Text),
            Claude = Path.GetFullPath(claude.Text),
            Agent = agent.Text.Trim(),
            Prompt = prompt.Text,
            ScheduledUtc = DateTime.UtcNow,
            Daily = true,
            DailyHour = runTime.Hours,
            DailyMinute = runTime.Minutes
        };
        var scheduler = new WindowsScheduler();
        job.Save();
        try { scheduler.Register(job); }
        catch
        {
            File.Delete(Path.Combine(job.Folder, "job.xml"));
            throw;
        }
        RefreshJobs(job.Id);
        registrationStatus.Text = "Scheduled. Check the status in the list below.";
    }

    private void RefreshJobs()
    {
        RefreshJobs(jobs.Selected?.Job.Id);
    }

    private void RefreshJobs(string selected)
    {
        if (!Directory.Exists(Job.Root)) return;
        var scheduler = new WindowsScheduler();
        var rows = Directory.EnumerateDirectories(Job.Root)
                .Where(folder => File.Exists(Path.Combine(folder, "job.xml")))
                .Select(folder => Job.Load(Path.GetFileName(folder)))
                .Where(job => !File.Exists(job.CanceledPath))
                .OrderByDescending(job => job.ScheduledUtc)
                .Select(job =>
            {
                string recordedResult = !job.Daily && File.Exists(job.ResultPath)
                    ? DisplayResult(File.ReadAllText(job.ResultPath)) : null;
                if (recordedResult == "Canceled") return null;
                bool? enabled = null;
                string status = recordedResult ?? scheduler.Status(job, out enabled);
                string schedule = job.Daily ? "Daily at " + DateTime.Today.AddHours(job.DailyHour)
                    .AddMinutes(job.DailyMinute).ToString("h:mm tt", CultureInfo.InvariantCulture)
                    : job.ScheduledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                return new ScheduleList.Row(job, schedule, status, enabled);
            }).Where(row => row != null).ToArray();
        jobs.ShowRows(rows, selected);
        UpdateSelection();
    }

    private static string DisplayResult(string result)
    {
        // Result files written by earlier versions may contain Korean text.
        if (result == "완료") return "Completed";
        if (result == "취소됨") return "Canceled";
        const string legacyFailure = "실패 (";
        return result.StartsWith(legacyFailure, StringComparison.Ordinal)
            ? "Failed (" + result.Substring(legacyFailure.Length) : result;
    }

    private Job Selected()
    {
        if (jobs.Selected == null) throw new InvalidOperationException("Select a schedule from the list.");
        return jobs.Selected.Job;
    }

    private void OpenLog()
    {
        string path = Selected().LogPath;
        if (!File.Exists(path)) throw new InvalidOperationException("The run log is not available yet.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void Cancel()
    {
        Job job = Selected();
        if (!job.Daily && File.Exists(job.ResultPath)) throw new InvalidOperationException("This schedule has already ended.");
        new WindowsScheduler().Cancel(job);
        RefreshJobs();
    }
}
