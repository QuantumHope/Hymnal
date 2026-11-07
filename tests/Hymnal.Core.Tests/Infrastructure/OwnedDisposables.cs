namespace Hymnal.Core.Tests.Infrastructure;

/// <summary>
/// Collects the objects a test harness creates and owns, and disposes them in reverse order.
/// Use it for watcher owners (<c>ManuscriptService</c>, <c>EditorViewModel</c>,
/// <c>GitPanelViewModel</c>): each live <c>FileSystemWatcher</c> holds an inotify instance on
/// Linux, and leaking them across a full run exhausts <c>fs.inotify.max_user_instances</c>.
/// Dispose this before deleting the harness's temp folders.
/// </summary>
public sealed class OwnedDisposables : IDisposable
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _items = new();

    public T Track<T>(T item) where T : IDisposable
    {
        lock (_gate)
            _items.Add(item);
        return item;
    }

    public void Dispose()
    {
        IDisposable[] items;
        lock (_gate)
        {
            items = _items.ToArray();
            _items.Clear();
        }

        List<Exception>? errors = null;
        for (var i = items.Length - 1; i >= 0; i--)
        {
            try
            {
                items[i].Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }
        }

        if (errors != null)
            throw new AggregateException("Disposing test-owned objects failed.", errors);
    }
}
