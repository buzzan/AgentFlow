using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AgentFlow;

public sealed partial class ScheduleCenterApp : Application
{
    private ScheduleCenter window;

    private ScheduleCenterApp() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new ScheduleCenter();
            window.Activate();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
            Exit();
        }
    }

    internal static void Run()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            try { new ScheduleCenterApp(); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                if (Current != null) Current.Exit();
                Environment.ExitCode = 1;
            }
        });
    }

}

internal sealed class ScheduleCenter : Window
{
    private readonly Grid page = new() { Padding = new Thickness(26, 20, 26, 22), RowSpacing = 14 };
    private readonly InfoBar feedback = new() { IsClosable = true };
    private readonly ScheduleTable scheduleTable = new();
    private ListView list => scheduleTable.List;
    private readonly SemaphoreSlim scheduleOperations = new(1);
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock enabledCount = Text("0", 26, true);
    private readonly TextBlock nextTime = Text("—", 26, true);
    private readonly TextBlock nextAgent = Text("Next run");
    private readonly TextBlock failedCount = Text("0", 26, true);
    private readonly TextBlock selection = Text("No schedule selected");
    private readonly SelectorBar filters = new();
    private readonly Button openLog;
    private readonly Button cancel;
    private readonly TextBox project = new() { Header = "Project" };
    private readonly TextBox claude = new() { Header = "Claude CLI" };
    private readonly AutoSuggestBox agent = new() { Header = "Agent", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimePicker time = new() { Header = "Daily Run Time", ClockIdentifier = "24HourClock" };
    private readonly TextBox prompt = new() { Header = "Task Instructions", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 82 };
    private readonly NavigationView navigation = new() { IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false };
    private readonly NavigationViewItem schedules = new() { Content = "Schedules", Icon = new SymbolIcon(Symbol.Calendar) };
    private readonly NavigationViewItem history = new() { Content = "Run history", Icon = new SymbolIcon(Symbol.Document) };
    private readonly Button create;
    private readonly Style cardStyle;
    private readonly Style mutedStyle;
    private Schedule[] rows = Array.Empty<Schedule>();
    private string[] foundAgents = Array.Empty<string>();
    private int agentSearch;
    private string scheduleSelection;
    private bool changingRows;
    private bool showingHistory;

    internal ScheduleCenter()
    {
        Title = "AgentFlow";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "assets", "AgentFlow-Circle.ico"));
        cardStyle = (Style)Application.Current.Resources["ScheduleCard"];
        mutedStyle = (Style)Application.Current.Resources["ScheduleSecondaryText"];
        nextAgent.Style = mutedStyle;
        create = Command("NewSchedule", "New schedule", Symbol.Add, ShowEditor, primary: true);
        var refresh = Command("RefreshSchedules", "Refresh", Symbol.Refresh, () => RefreshRows());
        openLog = Command("OpenRunLog", "Open Log", Symbol.OpenFile, () =>
        {
            OpenLog();
            return Task.CompletedTask;
        });
        cancel = Command("CancelSchedule", "Cancel Schedule", null, ConfirmCancel);
        UpdateSelection();
        claude.Text = FindClaude();
        time.Time = DateTime.Now.AddMinutes(5).TimeOfDay;
        project.LostFocus += async (_, _) => await LoadAgents();
        var agentEditorStyle = new Style(typeof(TextBox));
        agentEditorStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, "Agent"));
        agent.TextBoxStyle = agentEditorStyle;
        agent.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var matches = foundAgents.Where(name => name.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            sender.ItemsSource = matches;
            sender.IsSuggestionListOpen = matches.Length > 0;
        };

        AutomationProperties.SetAutomationId(project, "ScheduleProject");
        AutomationProperties.SetAutomationId(claude, "ScheduleClaude");
        AutomationProperties.SetAutomationId(agent, "ScheduleAgent");
        AutomationProperties.SetAutomationId(time, "ScheduleTime");
        AutomationProperties.SetAutomationId(prompt, "ScheduleInstructions");
        AutomationProperties.SetAutomationId(navigation, "ScheduleNavigation");
        AutomationProperties.SetAutomationId(schedules, "Schedules");
        AutomationProperties.SetAutomationId(history, "RunHistory");
        AutomationProperties.SetAutomationId(filters, "ScheduleFilters");
        agentEditorStyle.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, "ScheduleAgentInput"));

        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(Text("Schedules", 30, true));
        heading.Children.Add(Muted("Manage daily Claude Code runs."));
        var toolbar = Actions(refresh, create);
        toolbar.VerticalAlignment = VerticalAlignment.Center;
        var header = TwoColumns(heading, toolbar);
        var cards = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < 3; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
        Place(cards, Card(Stat(enabledCount, Muted("Enabled schedules")), new Thickness(16, 10, 16, 10)), column: 0);
        Place(cards, Card(Stat(nextTime, nextAgent), new Thickness(16, 10, 16, 10)), column: 1);
        Place(cards, Card(Stat(failedCount, Muted("Failed last run")), new Thickness(16, 10, 16, 10)), column: 2);

        foreach (var (id, label) in new[] { ("AllSchedules", "All schedules"), ("Enabled", "Enabled"), ("Paused", "Paused") })
        {
            var filter = new SelectorBarItem { Text = label };
            AutomationProperties.SetAutomationId(filter, id);
            filters.Items.Add(filter);
        }
        filters.SelectedItem = filters.Items[0];
        filters.Margin = new Thickness(12, 5, 12, 3);
        filters.SelectionChanged += (_, _) => ShowRows(scheduleSelection);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition());
        Place(table, filters);
        scheduleTable.SetEnabledAsync = (row, enabled) => RefreshRows(
            mutation: () => new WindowsScheduler().SetEnabled(row.Job, enabled));
        scheduleTable.OpenLog = row => Safely(() => OpenLog(row));
        list.SelectionChanged += (_, _) =>
        {
            if (changingRows) return;
            if (!showingHistory) scheduleSelection = Selected?.Job.Id;
            UpdateSelection();
        };
        Place(table, scheduleTable, 1);
        Place(table, empty, 1);

        selection.VerticalAlignment = VerticalAlignment.Center;
        selection.TextTrimming = TextTrimming.CharacterEllipsis;
        var footer = TwoColumns(selection, Actions(openLog, cancel));
        for (int i = 0; i < 5; i++)
            page.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Place(page, header);
        Place(page, cards, 1);
        Place(page, feedback, 2);
        Place(page, Card(table), 3);
        Place(page, Card(footer, new Thickness(12, 7, 12, 7)), 4);

        navigation.OpenPaneLength = 174;
        navigation.CompactPaneLength = 48;
        navigation.CompactModeThresholdWidth = 0;
        navigation.ExpandedModeThresholdWidth = 1000;
        navigation.MenuItems.Add(schedules);
        navigation.MenuItems.Add(history);
        navigation.SelectedItem = schedules;
        navigation.Content = page;
        navigation.SelectionChanged += (_, args) =>
        {
            if (changingRows) return;
            showingHistory = ReferenceEquals(args.SelectedItem, history);
            scheduleTable.ShowingHistory = showingHistory;
            ((TextBlock)heading.Children[0]).Text = showingHistory ? "Run history" : "Schedules";
            ((TextBlock)heading.Children[1]).Text = showingHistory ? "Latest available log for each schedule." : "Manage daily Claude Code runs.";
            cards.Visibility = filters.Visibility = cancel.Visibility = create.Visibility =
                showingHistory ? Visibility.Collapsed : Visibility.Visible;
            ShowRows(showingHistory ? null : scheduleSelection);
        };
        Content = navigation;
        var hwnd = WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1160 * scale), (int)(730 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(740 * scale);
            presenter.PreferredMinimumHeight = (int)(530 * scale);
        }
        page.Loaded += async (_, _) => await RefreshRows();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private Schedule Selected => list.SelectedItem as Schedule;

    private async Task<bool> RefreshRows(string selected = null, Action mutation = null, bool keepSelection = true)
    {
        await scheduleOperations.WaitAsync();
        try
        {
            var replacement = await Task.Run(() =>
            {
                mutation?.Invoke();
                return ScheduleStore.Load();
            });
            rows = replacement;
            UpdateCards();
            ShowRows(selected ?? (keepSelection ? (showingHistory ? Selected?.Job.Id : scheduleSelection) : null));
            return true;
        }
        catch (Exception error)
        {
            Notify("AgentFlow", error.Message, InfoBarSeverity.Error);
            return false;
        }
        finally { scheduleOperations.Release(); }
    }

    private void UpdateCards()
    {
        var active = rows.Where(row => !row.Canceled).ToArray();
        enabledCount.Text = active.Count(row => row.Enabled == true).ToString(CultureInfo.CurrentCulture);
        failedCount.Text = active.Count(row => row.Failed).ToString(CultureInfo.CurrentCulture);
        var next = active.Where(row => row.NextRun.HasValue).MinBy(row => row.NextRun);
        nextTime.Text = next?.NextRun?.ToString("HH:mm") ?? "—";
        nextAgent.Text = next == null ? "Next run" : "Next run · " + next.Job.Agent;
        ToolTipService.SetToolTip(nextTime, next?.NextRun?.ToString("f"));
    }

    private void ShowRows(string selected)
    {
        IEnumerable<Schedule> visible = rows;
        if (showingHistory) visible = rows.Where(row => row.Log != null).OrderByDescending(row => row.LastRun);
        else
        {
            visible = rows.Where(row => !row.Canceled);
            scheduleSelection = visible.Any(row => row.Job.Id == selected) ? selected : null;
            if (filters.SelectedItem == filters.Items[1]) visible = visible.Where(row => row.Enabled == true);
            else if (filters.SelectedItem == filters.Items[2]) visible = visible.Where(row => row.Enabled == false);
        }

        changingRows = true;
        try
        {
            var items = visible.ToArray();
            list.ItemsSource = items;
            list.SelectedItem = items.FirstOrDefault(item => item.Job.Id == selected);
            if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
            empty.Text = showingHistory ? "No run logs yet."
                : !rows.Any(row => !row.Canceled) ? "No schedules yet. Select New schedule to get started." : "No schedules match this filter.";
            empty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { changingRows = false; }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var row = Selected;
        selection.Text = row == null ? "No schedule selected" : row.Job.Agent + " · " + row.Status;
        openLog.IsEnabled = row != null;
        cancel.IsEnabled = !showingHistory && row?.CanCancel == true;
        ToolTipService.SetToolTip(selection, selection.Text);
    }

    private void OpenLog() => OpenLog(Selected);

    private static void OpenLog(Schedule row)
    {
        string path = row?.Job.LogPath;
        if (path == null || !File.Exists(path)) throw new InvalidOperationException("The run log is not available yet.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private async Task ConfirmCancel()
    {
        var row = Selected;
        if (row == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = page.XamlRoot, Title = "Cancel schedule?", DefaultButton = ContentDialogButton.Close,
            Content = "Future runs for “" + row.Job.Agent + "” will be canceled. Existing logs are kept.",
            PrimaryButtonText = "Cancel schedule", CloseButtonText = "Keep schedule"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (await RefreshRows(mutation: () => new WindowsScheduler().Cancel(row.Job), keepSelection: false))
            Notify("Schedule canceled", row.Job.Agent, InfoBarSeverity.Success);
    }

    private async Task ShowEditor()
    {
        var message = new InfoBar { Severity = InfoBarSeverity.Error };
        var form = new StackPanel { Spacing = 11 };
        form.Children.Add(message);
        form.Children.Add(TwoColumns(project, Command("BrowseProject", "Browse Folder", Symbol.Folder, BrowseProject)));
        form.Children.Add(TwoColumns(claude, Command("BrowseClaude", "Browse File", Symbol.OpenFile, BrowseClaude)));
        form.Children.Add(TwoColumns(agent, Command("ReloadAgents", "Reload Agents", Symbol.Refresh, () => LoadAgents(message))));
        form.Children.Add(time);
        form.Children.Add(prompt);
        var hint = Muted("Runs daily in Windows local time while you’re signed in, even if AgentFlow is closed.");
        hint.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = page.XamlRoot, Title = "New schedule", DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Schedule", CloseButtonText = "Close"
        };
        Job registered = null;
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                string projectPath = project.Text;
                string cliPath = claude.Text;
                string agentName = agent.Text.Trim();
                string instructions = prompt.Text;
                TimeSpan runTime = time.Time;
                registered = await Task.Run(() => ScheduleStore.Register(projectPath, cliPath, agentName, instructions, runTime));
            }
            catch (Exception error)
            {
                e.Cancel = true;
                Notify("AgentFlow", error.Message, InfoBarSeverity.Error, message);
            }
            finally { deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally
        {
            foreach (int index in new[] { 1, 2, 3 }) ((Grid)form.Children[index]).Children.Clear();
            form.Children.Clear();
        }
        if (registered == null) return;
        filters.SelectedItem = filters.Items[0];
        if (await RefreshRows(registered.Id)) Notify("Schedule created", registered.Agent, InfoBarSeverity.Success);
    }

    private async Task BrowseProject()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            project.Text = folder.Path;
            await LoadAgents();
        }
    }

    private async Task BrowseClaude()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file != null) claude.Text = file.Path;
    }

    private async Task LoadAgents(InfoBar target = null)
    {
        string projectPath = project.Text;
        int search = ++agentSearch;
        try
        {
            var matches = await Task.Run(() => FindAgents(projectPath));
            if (search != agentSearch || projectPath != project.Text) return;
            foundAgents = matches;
            agent.ItemsSource = matches;
            agent.IsSuggestionListOpen = false;
        }
        catch (Exception error)
        {
            if (search == agentSearch) Notify("AgentFlow", error.Message, InfoBarSeverity.Error, target);
        }
    }

    private static string[] FindAgents(string projectPath)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        string folder = Path.Combine(projectPath, ".claude", "agents");
        if (Directory.Exists(projectPath) && Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories))
            {
                using var file = File.OpenText(path);
                if ((file.ReadLine() ?? "").Trim() != "---") continue;
                string line;
                string name = "";
                while ((line = file.ReadLine()) != null && line.Trim() != "---")
                {
                    var match = Regex.Match(line, @"^name:\s*(.+)$");
                    if (match.Success) name = match.Groups[1].Value.Trim().Trim('"', '\'');
                }
                if (line != null && Job.ValidAgent(name)) names.Add(name);
            }
        }
        return names.ToArray();
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

    private bool Safely(Action action, InfoBar target = null)
    {
        try { action(); return true; }
        catch (Exception error)
        {
            Notify("AgentFlow", error.Message, InfoBarSeverity.Error, target);
            return false;
        }
    }

    private void Notify(string title, string message, InfoBarSeverity severity, InfoBar target = null)
    {
        var bar = target ?? feedback;
        bar.Title = title;
        bar.Message = message;
        bar.Severity = severity;
        bar.IsOpen = true;
    }

    private Button Command(string id, string name, Symbol? icon, Func<Task> execute, bool primary = false)
    {
        var button = new Button { Content = name, VerticalAlignment = VerticalAlignment.Bottom };
        if (icon.HasValue) button.Content = Actions(new SymbolIcon(icon.Value) { Width = 16 }, Text(name));
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        if (primary) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) =>
        {
            try { await execute(); }
            catch (Exception error) { Notify("AgentFlow", error.Message, InfoBarSeverity.Error); }
        };
        return button;
    }

    private static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    {
        Text = value, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
    private TextBlock Muted(string value) { var block = Text(value, 12); block.Style = mutedStyle; return block; }
    private Border Card(UIElement content, Thickness padding = default) => new() { Style = cardStyle, Padding = padding, Child = content };

    private static StackPanel Stat(params UIElement[] elements)
    {
        var panel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var element in elements) panel.Children.Add(element);
        return panel;
    }

    private static StackPanel Actions(params UIElement[] elements)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var element in elements) panel.Children.Add(element);
        return panel;
    }

    private static Grid TwoColumns(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(grid, left);
        Place(grid, right, column: 1);
        return grid;
    }

    private static void Place(Grid grid, FrameworkElement child, int row = 0, int column = 0)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }
}
