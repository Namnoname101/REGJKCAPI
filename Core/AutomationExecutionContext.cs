using System;
using System.Threading;
using System.Threading.Tasks;

namespace BAOCODETOOL.Core;

public sealed class AutomationExecutionContext
{
    private readonly ManualResetEventSlim _pauseEvent;

    public AutomationExecutionContext(
        CancellationToken cancellationToken,
        ManualResetEventSlim pauseEvent,
        IImageCaptchaSolver? imageCaptchaSolver = null,
        IGgGeeGuardTokenService? ggGeeGuardTokenService = null,
        ISliderCaptchaSolver? sliderCaptchaSolver = null)
    {
        CancellationToken = cancellationToken;
        _pauseEvent = pauseEvent;
        ImageCaptchaSolver = imageCaptchaSolver;
        GgGeeGuardTokenService = ggGeeGuardTokenService;
        SliderCaptchaSolver = sliderCaptchaSolver;
    }

    public CancellationToken CancellationToken { get; }

    public IImageCaptchaSolver? ImageCaptchaSolver { get; }

    public IGgGeeGuardTokenService? GgGeeGuardTokenService { get; }

    public ISliderCaptchaSolver? SliderCaptchaSolver { get; }

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
