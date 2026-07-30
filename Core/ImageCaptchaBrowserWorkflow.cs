using OpenQA.Selenium;

namespace BAOCODETOOL.Core;

internal static class ImageCaptchaBrowserWorkflow
{
    private const string FindCaptchaElementsScript =
        """
        const normalize = value => String(value || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .trim()
            .toLowerCase();

        const visible = element => {
            if (!element) return false;
            const style = window.getComputedStyle(element);
            if (style.display === 'none' || style.visibility === 'hidden') return false;
            const rect = element.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
        };

        const inputHint = input => normalize([
            input.id,
            input.name,
            input.placeholder,
            input.getAttribute('aria-label'),
            input.getAttribute('autocomplete')
        ].filter(Boolean).join(' '));

        const inputScore = input => {
            const hint = inputHint(input);
            let score = 0;
            if (hint.includes('captcha')) score += 12;
            if (hint.includes('ma xac minh')) score += 10;
            if (hint.includes('verification code')) score += 8;
            if (hint.includes('verify code')) score += 8;
            if (hint.includes('security code')) score += 7;
            return score;
        };

        const inputs = Array.from(document.querySelectorAll('input'))
            .filter(input => visible(input)
                && !input.disabled
                && String(input.type || '').toLowerCase() !== 'hidden')
            .map(input => ({ input, score: inputScore(input) }))
            .filter(candidate => candidate.score > 0);

        const images = Array.from(document.querySelectorAll('img, canvas'))
            .filter(image => {
                if (!visible(image)) return false;
                const rect = image.getBoundingClientRect();
                return rect.width >= 30
                    && rect.width <= 420
                    && rect.height >= 15
                    && rect.height <= 180;
            });

        let best = null;
        for (const candidate of inputs) {
            const inputRect = candidate.input.getBoundingClientRect();
            for (const image of images) {
                const imageRect = image.getBoundingClientRect();
                const horizontalGap = Math.max(
                    0,
                    Math.max(inputRect.left, imageRect.left)
                    - Math.min(inputRect.right, imageRect.right));
                const verticalGap = Math.abs(
                    (inputRect.top + inputRect.height / 2)
                    - (imageRect.top + imageRect.height / 2));
                if (horizontalGap > 500 || verticalGap > 220) continue;

                const imageHint = normalize([
                    image.id,
                    image.className,
                    image.getAttribute('alt'),
                    image.getAttribute('src')
                ].filter(Boolean).join(' '));
                let score = candidate.score;
                if (imageHint.includes('captcha')) score += 12;
                if (imageHint.includes('verify')) score += 6;
                if (candidate.input.parentElement?.contains(image)
                    || image.parentElement?.contains(candidate.input)) score += 10;
                score += Math.max(0, 8 - Math.floor(horizontalGap / 60));
                score += Math.max(0, 8 - Math.floor(verticalGap / 25));

                if (!best || score > best.score) {
                    best = { input: candidate.input, image, score };
                }
            }
        }

        return best ? { input: best.input, image: best.image } : null;
        """;

    private const string SetInputValueScript =
        """
        const input = arguments[0];
        const value = String(arguments[1] || '').trim();
        const descriptor = Object.getOwnPropertyDescriptor(
            HTMLInputElement.prototype,
            'value');
        if (descriptor && descriptor.set) {
            descriptor.set.call(input, value);
        } else {
            input.value = value;
        }
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
        input.dispatchEvent(new Event('blur', { bubbles: true }));
        return input.value;
        """;

    private const string ReadRenderedImageDataUrlScript =
        """
        const image = arguments[0];
        try {
            if (image instanceof HTMLCanvasElement) {
                return image.toDataURL('image/png');
            }

            if (!(image instanceof HTMLImageElement)) return null;
            const source = String(image.currentSrc || image.src || '');
            if (/^data:image\/(png|jpe?g|gif|webp);base64,/i.test(source)) {
                return source;
            }

            const width = image.naturalWidth || image.width || image.clientWidth;
            const height = image.naturalHeight || image.height || image.clientHeight;
            if (!width || !height) return null;

            const canvas = document.createElement('canvas');
            canvas.width = width;
            canvas.height = height;
            const context = canvas.getContext('2d');
            if (!context) return null;
            context.drawImage(image, 0, 0, width, height);
            return canvas.toDataURL('image/png');
        } catch {
            // Cross-origin images can taint canvas. Selenium screenshot is the fallback.
            return null;
        }
        """;

    public static async Task<ImageCaptchaFillResult> SolveAndFillAsync(
        IWebDriver driver,
        IImageCaptchaSolver solver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(solver);

        if (driver is not IJavaScriptExecutor javaScript)
        {
            return ImageCaptchaFillResult.Failure(
                "Browser does not support JavaScript captcha detection.");
        }

        var elements = javaScript.ExecuteScript(FindCaptchaElementsScript);
        if (elements is not IReadOnlyDictionary<string, object> values
            || !values.TryGetValue("input", out var inputValue)
            || inputValue is not IWebElement input
            || !values.TryGetValue("image", out var imageValue)
            || imageValue is not IWebElement image)
        {
            return ImageCaptchaFillResult.Failure(
                "Could not find an image captcha next to the verification input.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        javaScript.ExecuteScript(
            "arguments[0].scrollIntoView({block:'center', inline:'center'});",
            image);

        var imageBase64 = TryReadImageBase64(javaScript, image);
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            try
            {
                if (image is not ITakesScreenshot screenshotElement)
                {
                    return ImageCaptchaFillResult.Failure(
                        "The captcha element cannot be captured by Selenium.");
                }

                imageBase64 = Convert.ToBase64String(
                    screenshotElement.GetScreenshot().AsByteArray);
            }
            catch (WebDriverException exception)
            {
                return ImageCaptchaFillResult.Failure(
                    $"Could not capture the captcha image: {exception.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return ImageCaptchaFillResult.Failure("Captured captcha image is empty.");
        }

        var solution = await solver.SolveAsync(imageBase64, cancellationToken)
            .ConfigureAwait(false);
        if (!solution.IsSuccess)
        {
            return ImageCaptchaFillResult.Failure(solution.Message, solution.TaskId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var filledValue = javaScript.ExecuteScript(
                SetInputValueScript,
                input,
                solution.Text)
            ?.ToString();
        if (!string.Equals(
                filledValue?.Trim(),
                solution.Text.Trim(),
                StringComparison.Ordinal))
        {
            return ImageCaptchaFillResult.Failure(
                "Captcha was solved but the verification input rejected the value.",
                solution.TaskId);
        }

        return new ImageCaptchaFillResult(
            true,
            solution.Text,
            string.Empty,
            solution.TaskId);
    }

    private static string TryReadImageBase64(
        IJavaScriptExecutor javaScript,
        IWebElement image)
    {
        try
        {
            var dataUrl = javaScript.ExecuteScript(
                    ReadRenderedImageDataUrlScript,
                    image)
                ?.ToString();
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                return string.Empty;
            }

            var commaIndex = dataUrl.IndexOf(',');
            return commaIndex >= 0 && commaIndex < dataUrl.Length - 1
                ? dataUrl[(commaIndex + 1)..]
                : string.Empty;
        }
        catch (WebDriverException)
        {
            return string.Empty;
        }
    }
}

internal sealed record ImageCaptchaFillResult(
    bool IsSuccess,
    string Text,
    string Message,
    string? TaskId)
{
    public static ImageCaptchaFillResult Failure(
        string message,
        string? taskId = null) =>
        new(false, string.Empty, message, taskId);
}
