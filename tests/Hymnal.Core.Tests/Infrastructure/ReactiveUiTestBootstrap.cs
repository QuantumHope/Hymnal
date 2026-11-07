using System.Threading;
using Avalonia.Headless;
using ReactiveUI.Builder;

namespace Hymnal.Core.Tests.Infrastructure;

public static class ReactiveUiTestBootstrap
{
    private static readonly object Gate = new();
    private static HeadlessUnitTestSession? _session;
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (Gate)
        {
            if (_initialized)
                return;

            // Default (PerTest) isolation: Application.Current is null outside a Dispatch, which
            // production code relies on to run dispatcher work inline on plain test threads.
            // PerAssembly keeps one Application alive, so off-UI-thread tests that hit
            // Dispatcher.UIThread.InvokeAsync(...).GetResult() block forever (MainWindow*,
            // Corkboard/Gantt view smoke tests). Each Dispatch gets a fresh dispatcher, so a test
            // must wait for background UI work inside the same Dispatch that started it.
            _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));

            RxAppBuilder.CreateReactiveUIBuilder()
                .WithCoreServices()
                .BuildApp();

            _initialized = true;
        }
    }

    public static void RunOnUiThread(Action action)
    {
        EnsureInitialized();
        _session!.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static T RunOnUiThread<T>(Func<T> action)
    {
        EnsureInitialized();
        T? result = default;
        _session!.Dispatch(() => result = action(), CancellationToken.None).GetAwaiter().GetResult();
        return result!;
    }

    /// <summary>
    /// Runs an async operation on the headless UI thread; the session keeps the dispatcher
    /// loop running until the returned task completes, so UI-context continuations and
    /// <c>Dispatcher.UIThread.InvokeAsync</c> callbacks are processed.
    /// </summary>
    public static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        EnsureInitialized();
        return _session!.Dispatch(action, CancellationToken.None);
    }

    /// <summary>
    /// Like <see cref="RunOnUiThreadAsync{T}(Func{Task{T}})"/>, but bounded by <paramref name="timeout"/>.
    /// The token handed to <c>Dispatch</c> stops the session pumping the dispatcher for a stuck job;
    /// the test then fails instead of hanging. Note: in Avalonia.Headless 12.0.4 a job whose task never
    /// completes still occupies the session queue after cancellation (Dispatch blocks on the task), so
    /// later UI-thread tests in the same run can still stall — the timeout only makes the culprit visible.
    /// </summary>
    public static async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action, TimeSpan timeout)
    {
        EnsureInitialized();
        using var cts = new CancellationTokenSource(timeout);
        var task = _session!.Dispatch(action, cts.Token);
        if (await Task.WhenAny(task, Task.Delay(timeout + TimeSpan.FromSeconds(5))) != task)
            Assert.Fail($"UI-thread operation did not complete within {timeout.TotalSeconds:0}s.");
        return await task;
    }
}
