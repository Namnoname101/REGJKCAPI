namespace BAOCODETOOL.Core;

public interface IImageCaptchaSolver
{
    bool IsConfigured { get; }

    Task<ImageCaptchaSolveResult> SolveAsync(
        string imageBase64,
        CancellationToken cancellationToken);
}

public sealed record ImageCaptchaSolveResult(
    bool IsSuccess,
    string Text,
    string Message,
    string? TaskId,
    TimeSpan Elapsed)
{
    public static ImageCaptchaSolveResult Failure(
        string message,
        string? taskId = null,
        TimeSpan elapsed = default) =>
        new(false, string.Empty, message, taskId, elapsed);
}
