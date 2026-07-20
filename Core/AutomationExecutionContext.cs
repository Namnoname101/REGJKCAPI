using System;
using System.Threading;
using System.Threading.Tasks;

namespace VibecodeReg.Core;

public sealed class AutomationExecutionContext
{
    private readonly ManualResetEventSlim _pauseEvent;

    public AutomationExecutionContext(CancellationToken cancellationToken, ManualResetEventSlim pauseEvent)
    {
        CancellationToken = cancellationToken;
        _pauseEvent = pauseEvent;
    }

    public CancellationToken CancellationToken { get; }

    public void Checkpoint()
    {
        CancellationToken.ThrowIfCancellationRequested();
        _pauseEvent.Wait(CancellationToken);
    }

    public void Delay(TimeSpan delay)
    {
        Checkpoint();
        Task.Delay(delay, CancellationToken).GetAwaiter().GetResult();
        Checkpoint();
    }
}
