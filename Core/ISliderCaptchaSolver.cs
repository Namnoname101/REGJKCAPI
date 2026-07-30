namespace BAOCODETOOL.Core;

public interface ISliderCaptchaSolver
{
    bool IsConfigured { get; }

    Task<SliderCaptchaSolveResult> SolveAsync(
        string imageBase64,
        CancellationToken cancellationToken);
}

public sealed record SliderCaptchaSolveResult(
    bool IsSuccess,
    int EndX,
    int EndY,
    string Message,
    string? TaskId,
    TimeSpan Elapsed)
{
    public static SliderCaptchaSolveResult Failure(
        string message,
        string? taskId = null,
        TimeSpan elapsed = default) =>
        new(false, 0, 0, message, taskId, elapsed);
}
