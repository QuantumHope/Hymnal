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
}
