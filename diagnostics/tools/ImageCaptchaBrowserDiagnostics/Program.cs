using System.Reflection;
using BAOCODETOOL.Core;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

var options = new ChromeOptions();
options.AddArgument("--headless=new");
options.AddArgument("--disable-gpu");
options.AddArgument("--window-size=800,600");

using var driver = new ChromeDriver(options);
const string transparentPng =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
var html =
    $$"""
    <!doctype html>
    <html>
      <body>
        <div style="display:flex;align-items:center;gap:8px;margin:40px">
          <input id="verification" placeholder="Mã xác minh" />
          <img id="captcha-image"
               alt="captcha"
               src="data:image/png;base64,{{transparentPng}}"
               style="width:72px;height:30px;background:white;border:1px solid #ccc" />
        </div>
      </body>
    </html>
    """;
driver.Navigate().GoToUrl(
    "data:text/html;charset=utf-8," + Uri.EscapeDataString(html));

var workflowType = typeof(IImageCaptchaSolver).Assembly.GetType(
                       "BAOCODETOOL.Core.ImageCaptchaBrowserWorkflow")
                   ?? throw new InvalidOperationException(
                       "ImageCaptchaBrowserWorkflow was not found.");
var solveMethod = workflowType.GetMethod(
                      "SolveAndFillAsync",
                      BindingFlags.Public | BindingFlags.Static)
                  ?? throw new InvalidOperationException(
                      "SolveAndFillAsync was not found.");
var fakeSolver = new FakeImageCaptchaSolver();
var task = (Task)(solveMethod.Invoke(
                      null,
                      [driver, fakeSolver, CancellationToken.None])
                  ?? throw new InvalidOperationException(
                      "SolveAndFillAsync returned null."));
await task;

var result = task.GetType().GetProperty("Result")?.GetValue(task)
             ?? throw new InvalidOperationException("Captcha fill result was missing.");
var isSuccess = result.GetType().GetProperty("IsSuccess")?.GetValue(result) as bool?;
var inputValue = driver.FindElement(By.Id("verification")).GetAttribute("value");
if (isSuccess != true
    || inputValue != "4911"
    || fakeSolver.CapturedBase64 != transparentPng)
{
    throw new InvalidOperationException(
        $"Browser captcha flow failed. Success={isSuccess}, Value={inputValue}, DirectBase64={fakeSolver.CapturedBase64 == transparentPng}");
}

Console.WriteLine(
    "PASS browser image captcha: detected Vietnamese input, passed DOM Base64 directly, and filled 4911.");
return 0;

file sealed class FakeImageCaptchaSolver : IImageCaptchaSolver
{
    public bool IsConfigured => true;

    public string CapturedBase64 { get; private set; } = string.Empty;

    public Task<ImageCaptchaSolveResult> SolveAsync(
        string imageBase64,
        CancellationToken cancellationToken)
    {
        CapturedBase64 = imageBase64;
        return Task.FromResult(
            new ImageCaptchaSolveResult(
                true,
                "4911",
                string.Empty,
                "diagnostic-task",
                TimeSpan.Zero));
    }
}
