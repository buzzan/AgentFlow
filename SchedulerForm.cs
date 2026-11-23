using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal sealed class SchedulerForm : Form
{
    private readonly TextBox project = new TextBox();
    private readonly TextBox claude = new TextBox();
    private readonly ComboBox agent = new ComboBox();
    private readonly DateTimePicker date = new DateTimePicker();
    private readonly DateTimePicker time = new DateTimePicker();
    private readonly TextBox prompt = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly ListView jobs = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false };

    internal SchedulerForm()
    {
        Text = "Claude Code 에이전트 예약";
        Font = new Font("맑은 고딕", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(840, 620);
        MinimumSize = new Size(720, 560);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        Controls.Add(layout);
        AddRow(layout, "프로젝트", project, Button("폴더 선택", BrowseProject), 36);
        AddRow(layout, "Claude CLI", claude, Button("파일 선택", BrowseClaude), 36);
        AddRow(layout, "에이전트", agent, Button("목록 갱신", LoadAgents), 36);
        agent.DropDownStyle = ComboBoxStyle.DropDown;
        project.Leave += delegate { Safe(LoadAgents); };
        claude.Text = FindClaude();

        date.Format = DateTimePickerFormat.Short;
        time.Format = DateTimePickerFormat.Custom;
        time.CustomFormat = "HH:mm";
        time.ShowUpDown = true;
        DateTime initialTime = DateTime.Now.AddMinutes(5);
        date.Value = initialTime;
        time.Value = initialTime;
        var clock = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        date.Width = 145;
        time.Width = 85;
        clock.Controls.Add(date);
        clock.Controls.Add(time);
        clock.Controls.Add(new Label { AutoSize = true, Text = "Windows 현지 시간", Margin = new Padding(8, 6, 0, 0) });
        AddRow(layout, "실행 시각", clock, null, 36);
        AddRow(layout, "작업 지시", prompt, null, 112);
        AddRow(layout, "", new Label
        {
            Text = "예약 시각에 Windows에 로그인되어 있으면 앱을 닫아도 실행됩니다.",
            TextAlign = ContentAlignment.MiddleLeft
        }, Button("1회 예약", Register), 40);

        jobs.Dock = DockStyle.Fill;
        jobs.Columns.Add("실행 시각", 155);
        jobs.Columns.Add("에이전트", 150);
        jobs.Columns.Add("프로젝트", 310);
        jobs.Columns.Add("상태", 150);
        jobs.DoubleClick += delegate { Safe(OpenLog); };
        int listRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(jobs, 0, listRow);
        layout.SetColumnSpan(jobs, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("새로고침", RefreshJobs));
        buttons.Controls.Add(Button("로그 열기", OpenLog));
        buttons.Controls.Add(Button("예약 취소", Cancel));
        int buttonRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(buttons, 0, buttonRow);
        layout.SetColumnSpan(buttons, 3);
        Shown += delegate { Safe(RefreshJobs); };
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control field, Button button, int height)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        field.Dock = DockStyle.Fill;
        layout.Controls.Add(field, 1, row);
        if (button == null) layout.SetColumnSpan(field, 2);
        else layout.Controls.Add(button, 2, row);
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 29 };
        button.Click += delegate { Safe(action); };
        return button;
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
        if (!Directory.Exists(project.Text)) throw new ArgumentException("프로젝트 폴더를 선택하세요.");
        if (!File.Exists(claude.Text) || !String.Equals(Path.GetFileName(claude.Text), "claude.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Claude Code의 claude.exe 파일을 선택하세요.");
        if (!Job.ValidAgent(agent.Text.Trim())) throw new ArgumentException("올바른 에이전트 이름을 입력하세요.");
        if (String.IsNullOrWhiteSpace(prompt.Text)) throw new ArgumentException("작업 지시를 입력하세요.");
        DateTime local = date.Value.Date.AddHours(time.Value.Hour).AddMinutes(time.Value.Minute);
        if (local <= DateTime.Now) throw new ArgumentException("미래의 실행 시각을 선택하세요.");
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new ArgumentException("일광 절약 시간 전환 구간 밖의 시각을 선택하세요.");
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"),
            Project = Path.GetFullPath(project.Text),
            Claude = Path.GetFullPath(claude.Text),
            Agent = agent.Text.Trim(),
            Prompt = prompt.Text,
            ScheduledUtc = local.ToUniversalTime()
        };
        var scheduler = new WindowsScheduler();
        job.Save();
        try { scheduler.Register(job); }
        catch
        {
            File.Delete(Path.Combine(job.Folder, "job.xml"));
            throw;
        }
        RefreshJobs();
    }

    private void RefreshJobs()
    {
        if (!Directory.Exists(Job.Root)) return;
        var scheduler = new WindowsScheduler();
        string selected = jobs.SelectedItems.Count == 0 ? null : ((Job)jobs.SelectedItems[0].Tag).Id;
        jobs.BeginUpdate();
        try
        {
            jobs.Items.Clear();
            foreach (var job in Directory.EnumerateDirectories(Job.Root)
                .Where(folder => File.Exists(Path.Combine(folder, "job.xml")))
                .Select(folder => Job.Load(Path.GetFileName(folder))).OrderByDescending(job => job.ScheduledUtc))
            {
                string status = File.Exists(job.ResultPath) ? File.ReadAllText(job.ResultPath) : scheduler.Status(job);
                var item = new ListViewItem(new[]
                {
                    job.ScheduledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), job.Agent, job.Project, status
                }) { Tag = job };
                jobs.Items.Add(item);
                item.Selected = job.Id == selected;
            }
        }
        finally { jobs.EndUpdate(); }
    }

    private Job Selected()
    {
        if (jobs.SelectedItems.Count == 0) throw new InvalidOperationException("목록에서 예약을 선택하세요.");
        return (Job)jobs.SelectedItems[0].Tag;
    }

    private void OpenLog()
    {
        string path = Selected().LogPath;
        if (!File.Exists(path)) throw new InvalidOperationException("아직 실행 로그가 없습니다.");
        Process.Start(path);
    }

    private void Cancel()
    {
        Job job = Selected();
        if (File.Exists(job.ResultPath)) throw new InvalidOperationException("이미 종료된 예약입니다.");
        new WindowsScheduler().Cancel(job);
        RefreshJobs();
    }
}
