namespace HanYeong.Tests;

/// <summary>
/// The app's UI thread in the tests: work posted to it (the rest of a slow terminal replacement after each
/// delay, replayed keys) runs only when the test pumps it, on the test thread.
/// </summary>
sealed class ManualContext : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback, object?)> _work = new();

    public bool IsIdle { get { lock (_work) return _work.Count == 0; } }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_work) _work.Enqueue((d, state));
    }

    public void RunUntil(Func<bool> done, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            (SendOrPostCallback, object?)? next = null;
            lock (_work) if (_work.Count > 0) next = _work.Dequeue();
            if (next is var (d, state)) d(state);
            else Thread.Sleep(2);
        }
    }
}
