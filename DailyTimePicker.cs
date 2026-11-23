using System;
using System.Linq;
using System.Windows.Forms;

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Windows.Graphics;
using TimePicker = Microsoft.UI.Xaml.Controls.TimePicker;
using XamlControlsResources = Microsoft.UI.Xaml.Controls.XamlControlsResources;
using ToggleSwitch = Microsoft.UI.Xaml.Controls.ToggleSwitch;
using Xaml = Microsoft.UI.Xaml;
using XamlControls = Microsoft.UI.Xaml.Controls;

internal sealed class DailyTimePicker : WinUIHost
{
    private TimePicker picker;
    private TimeSpan time;

    internal DailyTimePicker()
    {
        Height = 58;
        TabStop = true;
    }

    internal TimeSpan Time
    {
        get { return picker == null ? time : picker.Time; }
        set
        {
            time = value;
            if (picker != null) picker.Time = time;
        }
    }

    protected override Microsoft.UI.Xaml.UIElement CreateContent()
    {
        // XamlControlsResources supplies the default template, including its
        // theme brushes, focus visuals, corner radius, and flyout.
        picker = new TimePicker
        {
            Header = "Daily Run Time",
            ClockIdentifier = "12HourClock",
            SelectedTime = time
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Daily Run Time");
        return picker;
    }

    protected override void OnHandleDestroyed(EventArgs args)
    {
        time = Time;
        picker = null;
        base.OnHandleDestroyed(args);
    }
}

internal sealed class ScheduleList : WinUIHost
{
    internal sealed class Row
    {
        internal readonly Job Job;
        internal readonly string Time;
        internal string Status;
        internal bool? Enabled;

        internal Row(Job job, string time, string status, bool? enabled)
        {
            Job = job;
            Time = time;
            Status = status;
            Enabled = enabled;
        }
    }

    private XamlControls.ListView list;
    private Row[] rows = new Row[0];
    private string selectedId;
    internal event Action SelectionChanged;
    internal event Action OpenLogRequested;
    internal event Action<Row, bool> ToggleRequested;
    internal Row Selected => (list?.SelectedItem as XamlControls.ListViewItem)?.Tag as Row;

    internal ScheduleList()
    {
        TabStop = true;
    }

    internal void ShowRows(Row[] updated, string selected)
    {
        rows = updated;
        selectedId = selected;
        if (list == null) return;
        var items = rows.Select(CreateRow).ToArray();
        list.ItemsSource = items;
        list.SelectedItem = items.FirstOrDefault(item => ((Row)item.Tag).Job.Id == selected);
        if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
    }

    protected override Xaml.UIElement CreateContent()
    {
        list = new XamlControls.ListView { TabFocusNavigation = Xaml.Input.KeyboardNavigationMode.Local };
        Xaml.Automation.AutomationProperties.SetName(list, AccessibleName);
        list.SelectionChanged += delegate { SelectionChanged?.Invoke(); };
        var header = Columns();
        header.Margin = new Xaml.Thickness(12, 0, 12, 0);
        string[] headings = { "Scheduled Time", "Agent", "Project", "Status", "Enabled" };
        for (int i = 0; i < headings.Length; i++) AddText(header, headings[i], i);
        ShowRows(rows, selectedId);
        var root = new XamlControls.Grid();
        root.RowDefinitions.Add(new XamlControls.RowDefinition { Height = Xaml.GridLength.Auto });
        root.RowDefinitions.Add(new XamlControls.RowDefinition());
        root.Children.Add(header);
        XamlControls.Grid.SetRow(list, 1);
        root.Children.Add(list);
        return root;
    }

    private XamlControls.ListViewItem CreateRow(Row row)
    {
        var grid = Columns();
        AddText(grid, row.Time, 0);
        AddText(grid, row.Job.Agent, 1);
        AddText(grid, row.Job.Project, 2);
        var status = AddText(grid, row.Status, 3);
        var item = new XamlControls.ListViewItem
        {
            Content = grid, Tag = row, HorizontalContentAlignment = Xaml.HorizontalAlignment.Stretch,
            Padding = new Xaml.Thickness(12, 0, 12, 0)
        };
        item.DoubleTapped += delegate { list.SelectedItem = item; OpenLogRequested?.Invoke(); };
        item.KeyDown += delegate(object sender, Xaml.Input.KeyRoutedEventArgs args)
        {
            if (args.Key != Windows.System.VirtualKey.Enter || args.OriginalSource is ToggleSwitch) return;
            list.SelectedItem = item;
            args.Handled = true;
            OpenLogRequested?.Invoke();
        };
        if (row.Enabled.HasValue)
        {
            var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = row.Enabled.Value, MinWidth = 0 };
            Xaml.Automation.AutomationProperties.SetName(toggle, "Enable " + row.Job.Agent + ", " + row.Time);
            toggle.DoubleTapped += delegate(object sender, Xaml.Input.DoubleTappedRoutedEventArgs args) { args.Handled = true; };
            toggle.Toggled += delegate
            {
                if (!row.Enabled.HasValue || toggle.IsOn == row.Enabled.Value) return;
                ToggleRequested?.Invoke(row, toggle.IsOn);
                toggle.IsEnabled = row.Enabled.HasValue;
                toggle.IsOn = row.Enabled.GetValueOrDefault();
                status.Text = row.Status;
            };
            XamlControls.Grid.SetColumn(toggle, 4);
            grid.Children.Add(toggle);
        }
        else AddText(grid, "—", 4);
        return item;
    }

    private static XamlControls.Grid Columns()
    {
        var grid = new XamlControls.Grid();
        foreach (int width in new[] { 150, 110, 0, 144, 90 })
            grid.ColumnDefinitions.Add(new XamlControls.ColumnDefinition
            {
                Width = width == 0 ? new Xaml.GridLength(1, Xaml.GridUnitType.Star) : new Xaml.GridLength(width)
            });
        return grid;
    }

    private static XamlControls.TextBlock AddText(XamlControls.Grid grid, string value, int column)
    {
        var text = new XamlControls.TextBlock
        {
            Text = value, TextTrimming = Xaml.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Xaml.VerticalAlignment.Center, Margin = new Xaml.Thickness(0, 0, 8, 0)
        };
        XamlControls.Grid.SetColumn(text, column);
        grid.Children.Add(text);
        return text;
    }

    protected override void OnHandleDestroyed(EventArgs args)
    {
        selectedId = Selected?.Job.Id;
        list = null;
        base.OnHandleDestroyed(args);
    }
}

internal abstract class WinUIHost : Control
{
    private DesktopWindowXamlSource island;

    protected abstract Microsoft.UI.Xaml.UIElement CreateContent();

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        island = new DesktopWindowXamlSource();
        island.Initialize(Win32Interop.GetWindowIdFromWindow(Handle));
        island.Content = CreateContent();
        island.TakeFocusRequested += delegate(DesktopWindowXamlSource sender, DesktopWindowXamlSourceTakeFocusRequestedEventArgs request)
        {
            var form = FindForm();
            if (form != null)
                form.SelectNextControl(this, request.Request.Reason != XamlSourceFocusNavigationReason.Last, true, true, true);
        };
        ResizeIsland();
    }

    protected override void OnGotFocus(EventArgs args)
    {
        base.OnGotFocus(args);
        if (island != null)
            island.NavigateFocus(new XamlSourceFocusNavigationRequest((ModifierKeys & Keys.Shift) != 0
                ? XamlSourceFocusNavigationReason.Last : XamlSourceFocusNavigationReason.First));
    }

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        ResizeIsland();
    }

    private void ResizeIsland()
    {
        if (island != null && ClientSize.Width > 0 && ClientSize.Height > 0)
            island.SiteBridge.MoveAndResize(new RectInt32(0, 0, ClientSize.Width, ClientSize.Height));
    }

    protected override void OnHandleDestroyed(EventArgs args)
    {
        if (island != null) island.Dispose();
        island = null;
        base.OnHandleDestroyed(args);
    }

    internal static void RunApplication()
    {
        var queue = DispatcherQueueController.CreateOnCurrentThread();
        try
        {
            var app = new PickerApplication();
            var messages = new IslandMessageFilter();
            using (WindowsXamlManager.InitializeForCurrentThread())
            {
                app.Resources.MergedDictionaries.Add(new XamlControlsResources());
                Application.AddMessageFilter(messages);
                try
                {
                    using (var form = new SchedulerForm()) Application.Run(form);
                }
                finally { Application.RemoveMessageFilter(messages); }
            }
        }
        finally { queue.ShutdownQueue(); }
    }

    private sealed class PickerApplication : Microsoft.UI.Xaml.Application, IXamlMetadataProvider
    {
        private readonly IXamlMetadataProvider metadata = new XamlControlsXamlMetaDataProvider();
        public IXamlType GetXamlType(Type type) { return metadata.GetXamlType(type); }
        public IXamlType GetXamlType(string name) { return metadata.GetXamlType(name); }
        public XmlnsDefinition[] GetXmlnsDefinitions() { return metadata.GetXmlnsDefinitions(); }
    }

    private sealed class IslandMessageFilter : IMessageFilter
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            internal IntPtr Window;
            internal uint Id;
            internal IntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal int X;
            internal int Y;
            internal uint Private;
        }

        [DllImport("Microsoft.UI.Windowing.Core.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ContentPreTranslateMessage(ref NativeMessage message);

        public bool PreFilterMessage(ref Message message)
        {
            var native = new NativeMessage
            {
                Window = message.HWnd, Id = (uint)message.Msg, WParam = message.WParam, LParam = message.LParam
            };
            return ContentPreTranslateMessage(ref native);
        }
    }
}
