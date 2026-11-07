using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.Core.Models;
using Hymnal.ViewModels;

namespace Hymnal.Views;

public partial class ChapterInfoView : UserControl
{
    public ChapterInfoView()
    {
        InitializeComponent();

        // Wire the status ComboBox SelectionChanged → SetStatusCommand.
        // SelectedValue is bound OneWay (Status has private setter), so user selections must be
        // forwarded to the command manually.  The guard `status != vm.DisplayStatus` prevents the
        // feedback loop when the VM itself updates Status (which re-selects the item).
        StatusComboBox.SelectionChanged += OnStatusSelectionChanged;

        // Bubbling LostFocus on the schedule table: when the user tabs or clicks out of any
        // schedule TextBox, trigger an immediate save rather than waiting for the debounce.
        PhaseScheduleTable.AddHandler(
            LostFocusEvent,
            OnScheduleField_LostFocus,
            RoutingStrategies.Bubble);
    }

    private void OnStatusSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ChapterInfoViewModel vm
            && e.AddedItems.Count > 0
            && e.AddedItems[0] is StatusOption { Value: var status }
            && status != vm.DisplayStatus)
        {
            // Fire-and-forget; command ThrownExceptions are handled inside the ViewModel.
            vm.SetStatusCommand.Execute(status).Subscribe(_ => { });
        }
    }

    private void OnScheduleField_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChapterInfoViewModel vm)
            vm.RequestImmediateScheduleSave();
    }
}
