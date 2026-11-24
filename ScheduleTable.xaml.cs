using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentFlow;

public sealed partial class ScheduleTable : UserControl
{
    internal ListView List => Rows;
    internal bool ShowingHistory { get; set; }
    internal Func<Schedule, bool, Task<bool>> SetEnabledAsync { get; set; }
    internal Action<Schedule> OpenLog { get; set; }

    public ScheduleTable() => InitializeComponent();

    private void InitializeToggle(object sender, RoutedEventArgs args) => UpdateToggle((ToggleSwitch)sender);
    private void ToggleDataChanged(FrameworkElement sender, DataContextChangedEventArgs args) => UpdateToggle((ToggleSwitch)sender);

    private void UpdateToggle(ToggleSwitch toggle)
    {
        var row = toggle.DataContext as Schedule;
        toggle.Tag = null;
        toggle.Visibility = !ShowingHistory && row?.Enabled != null ? Visibility.Visible : Visibility.Collapsed;
        toggle.IsOn = row?.Enabled == true;
        toggle.IsEnabled = true;
        toggle.Tag = row;
    }

    private async void ToggleChanged(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.Tag is not Schedule row || !ReferenceEquals(row, toggle.DataContext) ||
            row.Enabled == toggle.IsOn || SetEnabledAsync == null) return;
        toggle.IsEnabled = false;
        await SetEnabledAsync(row, toggle.IsOn);
        if (ReferenceEquals(row, toggle.DataContext)) UpdateToggle(toggle);
    }

    private void RowDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        args.Handled = OpenLogFrom(args.OriginalSource);
    }

    private void RowKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter) args.Handled = OpenLogFrom(args.OriginalSource);
    }

    private bool OpenLogFrom(object source)
    {
        for (var element = source as DependencyObject; element != null && element != Rows;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ToggleSwitch) return false;
            if (element is ListViewItem { Content: Schedule row })
            {
                Rows.SelectedItem = row;
                OpenLog?.Invoke(row);
                return true;
            }
        }
        return false;
    }
}
