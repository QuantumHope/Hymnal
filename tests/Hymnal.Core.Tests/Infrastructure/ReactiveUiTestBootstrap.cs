using System.Reflection;
using System.Threading;
using Avalonia.Headless;
using ReactiveUI.Builder;

namespace Hymnal.Core.Tests.Infrastructure;

public static class ReactiveUiTestBootstrap
{
    private static readonly object Gate = new();
    private static volatile HeadlessUnitTestSession? _session;
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
            _session = StartSession();

            RxAppBuilder.CreateReactiveUIBuilder()
                .WithCoreServices()
                .BuildApp();

            _initialized = true;
        }
    }

    public static void RunOnUiThread(Action action) =>
        RunOnUiThread(() =>
        {
            action();
            return 0;
        });

    public static T RunOnUiThread<T>(Func<T> action) =>
        DispatchAsync(() => Task.FromResult(action()), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Runs an async operation on the headless UI thread; the session keeps the dispatcher
    /// loop running until the returned task completes, so UI-context continuations and
    /// <c>Dispatcher.UIThread.InvokeAsync</c> callbacks are processed.
    /// </summary>
    public static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action) => DispatchAsync(action, CancellationToken.None);

    /// <summary>
    /// Like <see cref="RunOnUiThreadAsync{T}(Func{Task{T}})"/>, but bounded by <paramref name="timeout"/>.
    /// The token handed to <c>Dispatch</c> stops the session pumping the dispatcher for a stuck job;
    /// the test then fails instead of hanging. Note: in Avalonia.Headless 12.0.4 a job whose task never
    /// completes still occupies the session queue after cancellation (Dispatch blocks on the task), so
    /// later UI-thread tests in the same run can still stall — the timeout only makes the culprit visible.
    /// </summary>
    public static async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var task = DispatchAsync(action, cts.Token);
        if (await Task.WhenAny(task, Task.Delay(timeout + TimeSpan.FromSeconds(5))) != task)
            Assert.Fail($"UI-thread operation did not complete within {timeout.TotalSeconds:0}s.");
        return await task;
    }

    /// <summary>
    /// Dispatches <paramref name="action"/>, starting a new session when the current one's loop has died.
    /// <para>
    /// Between dispatches Avalonia has no UI-thread dispatcher, and view models reach
    /// <c>Dispatcher.UIThread</c> from thread-pool threads (background saves, loads and their
    /// notifications). A touch that lands while a dispatch is setting up its isolated application creates a
    /// dispatcher owned by that pool thread; the setup then throws ("a different thread owns it") outside
    /// the job's own error handling, which ends the session's loop: that dispatch, and every later one,
    /// would wait forever. When the setup failed before <paramref name="action"/> ran, it runs again on a
    /// fresh session; when the loop died after it ran (its teardown failed), the dispatch fails instead.
    /// </para>
    /// </summary>
    private static async Task<T> DispatchAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        while (true)
        {
            var session = _session!;
            var started = false;
            var dispatched = session.Dispatch(() =>
            {
                started = true;
                return action();
            }, cancellationToken);
            var loop = LoopOf(session);
            if (loop is null || await Task.WhenAny(dispatched, loop).ConfigureAwait(false) == dispatched)
                return await dispatched.ConfigureAwait(false);

            ReplaceDeadSession(session);
            if (Volatile.Read(ref started))
                throw new InvalidOperationException(
                    "The headless UI session ended while tearing down this dispatch.", loop.Exception?.InnerException);
        }
    }

    private static readonly FieldInfo? LoopField =
        typeof(HeadlessUnitTestSession).GetField("_dispatchTask", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The session's dispatch loop, which completes only when the loop ends (null if unavailable).</summary>
    private static Task? LoopOf(HeadlessUnitTestSession session) => LoopField?.GetValue(session) as Task;

    private static void ReplaceDeadSession(HeadlessUnitTestSession dead)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_session, dead))
                _session = StartSession();
        }
    }

    /// <summary>
    /// Starts a session whose loop can be watched. The session reads its loop task from a variable its own
    /// loop may start before it is assigned; such a session (rare) is shut down and another one started.
    /// </summary>
    private static HeadlessUnitTestSession StartSession()
    {
        while (true)
        {
            var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            if (LoopField is null || LoopOf(session) is not null)
                return session;

            try { session.Dispose(); }
            catch (NullReferenceException) { } // Dispose waits on the missing loop task after stopping the loop.
        }
    }
}
