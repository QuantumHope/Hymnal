using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.Core.Models;
using Hymnal.ViewModels;

namespace Hymnal.Views;

public partial class ChapterInfoWidget : UserControl
{
    public ChapterInfoWidget()
    {
        InitializeComponent();

        StatusComboBox.SelectionChanged += OnStatusSelectionChanged;

        PhaseScheduleTable.AddHandler(
            LostFocusEvent,
            OnScheduleField_LostFocus,
            RoutingStrategies.Bubble);
    }

    private void OnStatusSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ChapterDetailViewModel vm
            && e.AddedItems.Count > 0
            && e.AddedItems[0] is StatusOption { Value: var status }
            && status != vm.DisplayStatus)
        {
            vm.SetStatusCommand.Execute(status).Subscribe(_ => { }, _ => { });
        }
    }

    private void OnScheduleField_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChapterDetailViewModel vm)
            vm.RequestImmediateScheduleSave();
    }
}
