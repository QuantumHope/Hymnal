using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hymnal.Core.Models;
using Hymnal.ViewModels;
using Unit = System.Reactive.Unit;

namespace Hymnal.Views;

public partial class SupplementalDocsView : UserControl
{
    public SupplementalDocsView()
    {
        InitializeComponent();

        CreateDocsFolderButton.Click += CreateFolder_Click;
        CreateDocsFileButton.Click += CreateFile_Click;
        ImportExistingFileButton.Click += ImportExistingFile_Click;
        DocsTree.DoubleTapped += DocsTree_DoubleTapped;
        DocsTree.SelectionChanged += DocsTree_SelectionChanged;
    }

    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SupplementalDocsViewModel vm)
            return;

        var folderName = await PromptForTextAsync("Create docs folder", "Folder name:", "research");
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        await ExecuteCommandAsync(vm.CreateFolderCommand.Execute(folderName.Trim()));
    }

    private async void CreateFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SupplementalDocsViewModel vm)
            return;

        var fileName = await PromptForTextAsync("Create docs file", "File name:", "notes.md");
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        await ExecuteCommandAsync(vm.CreateFileCommand.Execute(fileName.Trim()));
    }

    private async void ImportExistingFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SupplementalDocsViewModel vm)
            return;

        await ExecuteCommandAsync(vm.ImportExistingFileCommand.Execute());
    }

    private async void DocsTree_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SupplementalDocsViewModel vm || DocsTree.SelectedItem is not SupplementalDocNode node)
            return;

        if (node.Kind != SupplementalDocNodeKind.File)
            return;

        await ExecuteCommandAsync(vm.OpenDocCommand.Execute(node));
    }

    private async void DocsTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SupplementalDocsViewModel vm || DocsTree.SelectedItem is not SupplementalDocNode node)
            return;

        if (node.Kind != SupplementalDocNodeKind.File)
            return;

        await ExecuteCommandAsync(vm.OpenDocCommand.Execute(node));
    }

    private async Task<string?> PromptForTextAsync(string title, string prompt, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = GetResource<Avalonia.Media.IBrush>("Cora.Surface.Panel")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#120932"))
        };

        var input = new TextBox
        {
            Text = initialValue,
            FontSize = 12,
            Padding = new Thickness(8, 6),
            Background = GetResource<Avalonia.Media.IBrush>("Cora.Surface.Void")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0F0828")),
            Foreground = GetResource<Avalonia.Media.IBrush>("Cora.Text")
                ?? Avalonia.Media.Brushes.White,
            BorderBrush = GetResource<Avalonia.Media.IBrush>("Cora.Border.Subtle")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D1B5E")),
            BorderThickness = new Thickness(1)
        };

        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? submitted = null;

        void CloseWithResult(string? value)
        {
            if (!result.Task.IsCompleted)
                result.TrySetResult(value);

            if (dialog.IsVisible)
                dialog.Close();
        }

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Background = GetResource<Avalonia.Media.IBrush>("Cora.Accent.Store")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9D4EDD")),
            Foreground = GetResource<Avalonia.Media.IBrush>("Cora.IconInk")
                ?? Avalonia.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6)
        };
        ok.Click += (_, _) =>
        {
            submitted = input.Text?.Trim();
            CloseWithResult(submitted);
        };

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(12, 6)
        };
        cancel.Click += (_, _) => CloseWithResult(null);

        dialog.Content = new Border
        {
            BorderBrush = GetResource<Avalonia.Media.IBrush>("Cora.Border.Subtle")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D1B5E")),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        Foreground = GetResource<Avalonia.Media.IBrush>("Cora.Text")
                            ?? Avalonia.Media.Brushes.White,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel }
                    }
                }
            }
        };

        dialog.Closed += (_, _) =>
        {
            if (!result.Task.IsCompleted)
                result.TrySetResult(submitted);
        };

        _ = dialog.ShowDialog(owner);
        return await result.Task;
    }

    private static async Task ExecuteCommandAsync(IObservable<Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    private T? GetResource<T>(string key) where T : class
    {
        return this.TryFindResource(key, out var value) && value is T typed
            ? typed
            : null;
    }
}
