using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Views;

namespace Hymnal.Infrastructure;

public sealed class ProjectInitPrompt : IProjectInitPrompt
{
    private readonly Func<Window?> _ownerAccessor;

    public ProjectInitPrompt(Func<Window?> ownerAccessor)
    {
        _ownerAccessor = ownerAccessor;
    }

    public Task<bool> ConfirmInitializeAsync(string folderPath) =>
        OnUiThread(async () =>
        {
            var owner = _ownerAccessor();
            return owner != null && await ConfirmInitializeDialog.ShowAsync(owner);
        });

    public Task<ProjectKind?> ChooseProjectKindAsync() =>
        OnUiThread(async () =>
        {
            var owner = _ownerAccessor();
            return owner == null ? null : await ProjectTypeDialog.ShowAsync(owner);
        });

    private static Task<T> OnUiThread<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
}
