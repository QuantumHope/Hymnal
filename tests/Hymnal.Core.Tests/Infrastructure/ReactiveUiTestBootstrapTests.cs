using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Xunit;

namespace Hymnal.Core.Tests.Infrastructure;

[Collection("AvaloniaUi")]
public sealed class ReactiveUiTestBootstrapTests
{
    /// <summary>
    /// View models touch <see cref="Dispatcher.UIThread"/> from thread-pool threads. Between dispatches no UI
    /// dispatcher exists, so such a touch creates one owned by that thread; when it lands while a dispatch is
    /// setting up its application, that setup fails and the headless session's loop dies, hanging every later
    /// dispatch. Dispatches must survive it.
    /// </summary>
    [Fact]
    public void Dispatches_survive_background_threads_touching_the_ui_dispatcher()
    {
        using var stop = new CancellationTokenSource();
        var touches = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = Dispatcher.UIThread;
                Thread.Sleep(1);
            }
        });

        try
        {
            for (var i = 0; i < 200; i++)
            {
                var ran = ReactiveUiTestBootstrap.RunOnUiThreadAsync(() => Task.FromResult(i), TimeSpan.FromSeconds(10));
                Assert.Equal(i, ran.GetAwaiter().GetResult());
            }
        }
        finally
        {
            stop.Cancel();
            touches.GetAwaiter().GetResult();
        }

        Assert.Equal(1, ReactiveUiTestBootstrap.RunOnUiThread(() => 1));
    }
}
