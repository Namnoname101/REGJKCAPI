using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BankDomSurvey;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SiteDefinition[] Sites =
    [
        new("RR88", "http://gioithieu.rr3311.com/?referralCode=mrh6472"),
        new("MM88", "http://gioithieu.mm88p.com/?referralCode=mgr0794"),
        new("XX88", "http://gioithieu.xx88.fun/?referralCode=gql2969")
    ];

    public static int Main(string[] args)
    {
        var options = SurveyOptions.Parse(args);
        Directory.CreateDirectory(options.OutputRoot);

        var reports = new List<SiteSurveyReport>();

        foreach (var site in Sites.Where(site => options.Sites.Contains(site.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var report = RunSiteSurvey(site, options);
            reports.Add(report);
        }

        WriteJson(Path.Combine(options.OutputRoot, "summary.json"), reports);
        WriteMarkdownSummary(Path.Combine(options.OutputRoot, "summary.md"), reports, options);

        Console.WriteLine($"Diagnostics written to: {options.OutputRoot}");
        return reports.Any(report => report.Accessible == false && report.Blockers.Count > 0) ? 2 : 0;
    }

    private static SiteSurveyReport RunSiteSurvey(SiteDefinition site, SurveyOptions options)
    {
        var siteDirectory = Path.Combine(options.OutputRoot, site.Name.ToLowerInvariant());
        Directory.CreateDirectory(siteDirectory);

        var report = new SiteSurveyReport
        {
            Site = site.Name,
            TargetUrl = site.Url,
            StartedAt = DateTimeOffset.Now
        };

        var profilePath = SelectProfilePath(options.ProfileRoot, site.Name);
        if (profilePath is null)
        {
            report.Blockers.Add($"No existing Chrome profile folder found for {site.Name}.");
            report.FinishedAt = DateTimeOffset.Now;
            WriteJson(Path.Combine(siteDirectory, "report.json"), report);
            return report;
        }

        report.ProfileFolder = MaskProfilePath(profilePath);

        try
        {
            using var driver = CreateDriver(profilePath, options);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(options.PageLoadTimeoutSeconds);

            driver.Navigate().GoToUrl(site.Url);
            WaitForReadyState(driver, TimeSpan.FromSeconds(options.PageLoadTimeoutSeconds));

            var afterOpen = CaptureSnapshot(driver, siteDirectory, "01-after-open");
            report.Accessible = true;
            report.AfterOpen = afterOpen;
            report.AuthenticatedLikely = afterOpen.AuthenticatedLikely;
            report.CaptchaOrOtpBlocker = afterOpen.BlockerLikely;
            report.Blockers.AddRange(afterOpen.Blockers);

            var bankCandidate = FindBestBankNavigationCandidate(afterOpen.Elements);
            if (bankCandidate is null)
            {
                report.Blockers.Add("No visible bank/account/wallet navigation candidate found in default document.");
            }
            else
            {
                var bankNavigationSelector = bankCandidate.CssPath!;
                report.BankNavigationAttempted = true;
                report.BankNavigationSelector = bankNavigationSelector;
                report.BankNavigationText = bankCandidate.SafeText;

                var beforeClickUrl = driver.Url;
                report.BankNavigationBeforeUrl = beforeClickUrl;

                ClickByCssPath(driver, bankNavigationSelector);
                WaitForReadyState(driver, TimeSpan.FromSeconds(Math.Min(options.PageLoadTimeoutSeconds, 15)));
                Thread.Sleep(TimeSpan.FromSeconds(2));

                report.BankNavigationAfterUrl = driver.Url;
                var afterBankNav = CaptureSnapshot(driver, siteDirectory, "02-after-bank-nav");
                report.AfterBankNavigation = afterBankNav;
                report.BankContainerSelectors.AddRange(FindBankContainerSelectors(afterBankNav.Elements));
                report.BankFormFields.AddRange(FindBankFormFields(afterBankNav.Elements));
                report.SubmitSelectors.AddRange(FindSubmitSelectors(afterBankNav.Elements));
                report.FailureSelectors.AddRange(FindFailureSelectors(afterBankNav.Elements));
                report.BankFormLikely = report.BankFormFields.Count > 0;

                if (!HasBankTestData())
                {
                    report.Blockers.Add("Bank test data env vars are missing; no field input or submit was attempted.");
                }
            }
        }
        catch (WebDriverException ex)
        {
            report.Accessible = false;
            report.Blockers.Add($"WebDriver failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            report.Accessible = false;
            report.Blockers.Add($"Survey failed: {ex.Message}");
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.Now;
            WriteJson(Path.Combine(siteDirectory, "report.json"), report);
        }

        return report;
    }

    private static IWebDriver CreateDriver(string profilePath, SurveyOptions options)
    {
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument($"--user-data-dir={profilePath}");
        chromeOptions.AddArgument("--disable-gpu");
        chromeOptions.AddArgument("--disable-dev-shm-usage");
        chromeOptions.AddArgument("--disable-notifications");
        chromeOptions.AddArgument("--disable-popup-blocking");
        chromeOptions.AddExcludedArgument("enable-automation");

        if (!options.Headed)
        {
            chromeOptions.AddArgument("--headless=new");
            chromeOptions.AddArgument("--window-size=1440,1100");
        }
        else
        {
            chromeOptions.AddArgument("--start-maximized");
        }

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;

        return new ChromeDriver(service, chromeOptions, TimeSpan.FromSeconds(90));
    }

    private static PageSnapshot CaptureSnapshot(IWebDriver driver, string siteDirectory, string stage)
    {
        var snapshot = new PageSnapshot
        {
            Stage = stage,
            CapturedAt = DateTimeOffset.Now,
            Url = driver.Url,
            Title = driver.Title,
            SourceFile = $"{stage}.html",
            ScreenshotFile = $"{stage}.png",
            ElementsFile = $"{stage}.elements.json",
            FrameFiles = []
        };

        var source = RedactSensitiveContent(driver.PageSource);
        File.WriteAllText(Path.Combine(siteDirectory, snapshot.SourceFile), source, Encoding.UTF8);

        if (driver is ITakesScreenshot takesScreenshot)
        {
            takesScreenshot.GetScreenshot().SaveAsFile(Path.Combine(siteDirectory, snapshot.ScreenshotFile));
        }

        snapshot.Elements = CollectElements(driver);
        snapshot.Iframes = CollectIframes(driver);
        snapshot.AuthenticatedSignals.AddRange(FindAuthenticatedSignals(snapshot.Elements));
        snapshot.AuthenticatedLikely = snapshot.AuthenticatedSignals.Count > 0;
        snapshot.Blockers.AddRange(FindManualBlockers(snapshot.Elements, source));
        snapshot.BlockerLikely = snapshot.Blockers.Count > 0;

        WriteJson(Path.Combine(siteDirectory, snapshot.ElementsFile), snapshot.Elements);

        for (var index = 0; index < snapshot.Iframes.Count; index++)
        {
            var iframe = snapshot.Iframes[index];
            var frameFilePrefix = $"{stage}.frame-{index}";

            try
            {
                driver.SwitchTo().DefaultContent();
                driver.SwitchTo().Frame(index);

                var frameSourceFile = $"{frameFilePrefix}.html";
                var frameElementsFile = $"{frameFilePrefix}.elements.json";
                File.WriteAllText(Path.Combine(siteDirectory, frameSourceFile), RedactSensitiveContent(driver.PageSource), Encoding.UTF8);
                WriteJson(Path.Combine(siteDirectory, frameElementsFile), CollectElements(driver));

                snapshot.FrameFiles.Add(new FrameCapture
                {
                    Index = index,
                    SourceFile = frameSourceFile,
                    ElementsFile = frameElementsFile,
                    Src = iframe.Src
                });
            }
            catch (WebDriverException ex)
            {
                snapshot.FrameFiles.Add(new FrameCapture
                {
                    Index = index,
                    Src = iframe.Src,
                    Error = ex.Message
                });
            }
            finally
            {
                driver.SwitchTo().DefaultContent();
            }
        }

        WriteJson(Path.Combine(siteDirectory, $"{stage}.meta.json"), snapshot.WithoutElementPayload());
        return snapshot.WithoutElementPayload();
    }

    private static IReadOnlyList<UiElementInfo> CollectElements(IWebDriver driver)
    {
        const string script = """
        const MAX_TEXT = 140;
        function esc(value) {
          if (window.CSS && CSS.escape) return CSS.escape(value);
          return String(value || '').replace(/[^a-zA-Z0-9_-]/g, '\\$&');
        }
        function visible(el) {
          const style = window.getComputedStyle(el);
          const rect = el.getBoundingClientRect();
          return style && style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
        }
        function cssPath(el) {
          if (!el || el.nodeType !== 1) return '';
          if (el.id) return '#' + esc(el.id);
          const parts = [];
          let node = el;
          while (node && node.nodeType === 1 && node !== document.body) {
            let selector = node.tagName.toLowerCase();
            if (node.getAttribute('name')) selector += '[name="' + esc(node.getAttribute('name')) + '"]';
            else {
              const siblings = Array.from(node.parentElement ? node.parentElement.children : [])
                .filter(s => s.tagName === node.tagName);
              if (siblings.length > 1) selector += ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')';
            }
            parts.unshift(selector);
            node = node.parentElement;
          }
          return parts.join(' > ');
        }
        function dataAttrs(el) {
          const data = {};
          for (const attr of Array.from(el.attributes || [])) {
            if (attr.name.startsWith('data-')) data[attr.name] = attr.value;
          }
          return data;
        }
        const selector = 'a,button,input,select,textarea,[role],[aria-label],[onclick],dialog,.modal,.popup,.registerModal,.bank,.wallet,.withdraw';
        return JSON.stringify(Array.from(document.querySelectorAll(selector)).slice(0, 900).map(el => {
          const text = (el.innerText || el.textContent || el.value || '').replace(/\s+/g, ' ').trim();
          return {
            tag: el.tagName.toLowerCase(),
            id: el.id || null,
            name: el.getAttribute('name'),
            type: el.getAttribute('type'),
            role: el.getAttribute('role'),
            ariaLabel: el.getAttribute('aria-label'),
            placeholder: el.getAttribute('placeholder'),
            className: el.getAttribute('class'),
            href: el.getAttribute('href'),
            title: el.getAttribute('title'),
            text: text.length > MAX_TEXT ? text.substring(0, MAX_TEXT) : text,
            cssPath: cssPath(el),
            visible: visible(el),
            enabled: !el.disabled,
            data: dataAttrs(el)
          };
        }));
        """;

        var json = (string)((IJavaScriptExecutor)driver).ExecuteScript(script);
        var elements = JsonSerializer.Deserialize<List<UiElementInfo>>(json, JsonOptions) ?? [];
        foreach (var element in elements)
        {
            element.Text = RedactSensitiveContent(element.Text ?? string.Empty);
        }

        return elements;
    }

    private static IReadOnlyList<IframeInfo> CollectIframes(IWebDriver driver)
    {
        const string script = """
        function visible(el) {
          const style = window.getComputedStyle(el);
          const rect = el.getBoundingClientRect();
          return style && style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
        }
        return JSON.stringify(Array.from(document.querySelectorAll('iframe')).map((el, index) => ({
          index,
          id: el.id || null,
          name: el.name || null,
          src: el.src || el.getAttribute('src'),
          title: el.title || null,
          className: el.getAttribute('class'),
          visible: visible(el)
        })));
        """;

        var json = (string)((IJavaScriptExecutor)driver).ExecuteScript(script);
        return JsonSerializer.Deserialize<List<IframeInfo>>(json, JsonOptions) ?? [];
    }

    private static UiElementInfo? FindBestBankNavigationCandidate(IReadOnlyList<UiElementInfo> elements)
    {
        return elements
            .Where(element => element.Visible && element.Enabled && !string.IsNullOrWhiteSpace(element.CssPath))
            .Select(element => new { Element = element, Score = ScoreBankNavigation(element) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Element.CssPath!.Length)
            .FirstOrDefault()
            ?.Element;
    }

    private static int ScoreBankNavigation(UiElementInfo element)
    {
        var haystack = NormalizeForMatch(element.SafeText + " " + element.AriaLabel + " " + element.Title + " " + element.Href + " " + element.ClassName);

        if (ContainsAny(haystack, "dang nhap", "dang ky", "submit", "save", "confirm", "xac nhan", "luu", "login", "register"))
        {
            return 0;
        }

        var score = 0;
        if (ContainsAny(haystack, "ngan hang", "bank")) score += 100;
        if (ContainsAny(haystack, "rut tien", "withdraw")) score += 85;
        if (ContainsAny(haystack, "vi", "wallet")) score += 70;
        if (ContainsAny(haystack, "tai khoan", "account", "member", "profile")) score += 45;
        if (ContainsAny(haystack, "ho so", "bao mat", "security")) score += 30;
        if (element.Tag is "a" or "button") score += 10;

        return score;
    }

    private static IReadOnlyList<string> FindAuthenticatedSignals(IReadOnlyList<UiElementInfo> elements)
    {
        return elements
            .Where(element => element.Visible)
            .Where(element =>
            {
                var haystack = NormalizeForMatch(element.SafeText + " " + element.AriaLabel + " " + element.ClassName + " " + element.Href);
                return ContainsAny(haystack, "dang xuat", "logout", "balance", "so du", "member center", "member-center", "avatar", "user-info", "profile-info");
            })
            .Select(element => $"{element.CssPath} :: {element.SafeText}")
            .Where(signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct()
            .Take(12)
            .ToArray();
    }

    private static IReadOnlyList<string> FindManualBlockers(IReadOnlyList<UiElementInfo> elements, string source)
    {
        var combined = NormalizeForMatch(source + " " + string.Join(" ", elements.Select(element => element.SafeText)));
        var blockers = new List<string>();

        if (ContainsAny(combined, "captcha", "recaptcha", "geetest"))
        {
            blockers.Add("Captcha/Geetest signal found in DOM.");
        }

        if (ContainsAny(combined, "otp", "ma xac minh", "verification code"))
        {
            blockers.Add("OTP/verification-code signal found in DOM.");
        }

        return blockers;
    }

    private static IReadOnlyList<string> FindBankContainerSelectors(IReadOnlyList<UiElementInfo> elements)
    {
        return elements
            .Where(element => element.Visible)
            .Where(element =>
            {
                var haystack = NormalizeForMatch(element.SafeText + " " + element.ClassName + " " + element.Id);
                return ContainsAny(haystack, "bank", "ngan hang", "wallet", "withdraw", "rut tien");
            })
            .Select(element => element.CssPath ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .Take(20)
            .ToArray();
    }

    private static IReadOnlyList<FieldCandidate> FindBankFormFields(IReadOnlyList<UiElementInfo> elements)
    {
        return elements
            .Where(element => element.Visible && element.Tag is "input" or "select" or "textarea")
            .Select(element => new FieldCandidate
            {
                CssPath = element.CssPath,
                Tag = element.Tag,
                Name = element.Name,
                Type = element.Type,
                Placeholder = element.Placeholder,
                AriaLabel = element.AriaLabel,
                LikelyPurpose = GuessFieldPurpose(element)
            })
            .Where(field => !string.IsNullOrWhiteSpace(field.LikelyPurpose))
            .Take(50)
            .ToArray();
    }

    private static IReadOnlyList<string> FindSubmitSelectors(IReadOnlyList<UiElementInfo> elements)
    {
        return elements
            .Where(element => element.Visible && element.Enabled)
            .Where(element =>
            {
                var haystack = NormalizeForMatch(element.SafeText + " " + element.Type + " " + element.ClassName);
                return ContainsAny(haystack, "save", "submit", "confirm", "luu", "xac nhan", "hoan tat", "tiep tuc");
            })
            .Select(element => $"{element.CssPath} :: {element.SafeText}")
            .Distinct()
            .Take(20)
            .ToArray();
    }

    private static IReadOnlyList<string> FindFailureSelectors(IReadOnlyList<UiElementInfo> elements)
    {
        return elements
            .Where(element => element.Visible)
            .Where(element =>
            {
                var haystack = NormalizeForMatch(element.SafeText + " " + element.ClassName + " " + element.Role);
                return ContainsAny(haystack, "error", "loi", "invalid", "khong hop le", "toast", "dialog", "vui long");
            })
            .Select(element => $"{element.CssPath} :: {element.SafeText}")
            .Distinct()
            .Take(20)
            .ToArray();
    }

    private static string? GuessFieldPurpose(UiElementInfo element)
    {
        var haystack = NormalizeForMatch(element.Name + " " + element.Placeholder + " " + element.AriaLabel + " " + element.ClassName + " " + element.Id);

        if (ContainsAny(haystack, "bank", "ngan hang")) return "bank-name/dropdown";
        if (ContainsAny(haystack, "account", "stk", "so tai khoan", "tai khoan")) return "account-number";
        if (ContainsAny(haystack, "name", "ten", "payee", "holder", "chu tai khoan")) return "account-holder";
        if (ContainsAny(haystack, "branch", "chi nhanh")) return "branch";
        if (ContainsAny(haystack, "pin", "withdraw", "rut tien", "level2", "security", "bao mat")) return "withdraw-pin/password";
        return null;
    }

    private static void ClickByCssPath(IWebDriver driver, string cssPath)
    {
        var element = driver.FindElement(By.CssSelector(cssPath));
        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({block:'center', inline:'center'});", element);
        Thread.Sleep(300);
        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element);
    }

    private static void WaitForReadyState(IWebDriver driver, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now.Add(timeout);

        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                var readyState = ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState")?.ToString();
                if (string.Equals(readyState, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (WebDriverException)
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static string? SelectProfilePath(string profileRoot, string siteName)
    {
        if (!Directory.Exists(profileRoot))
        {
            return null;
        }

        return Directory.GetDirectories(profileRoot)
            .Select(profile => Path.Combine(profile, siteName))
            .Where(Directory.Exists)
            .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private static bool HasBankTestData()
    {
        return HasEnv("BANK_TEST_NAME")
            && HasEnv("BANK_TEST_NUMBER")
            && HasEnv("BANK_TEST_PIN");
    }

    private static bool HasEnv(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static string MaskProfilePath(string path)
    {
        var directory = new DirectoryInfo(path);
        var site = directory.Name;
        var profile = directory.Parent?.Name ?? "unknown-profile";
        return Path.Combine("Profiles", profile, site);
    }

    private static string RedactSensitiveContent(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = Regex.Replace(value, "src=[\"']data:image/[^\"']+[\"']", "src=\"[redacted-data-image]\"", RegexOptions.IgnoreCase);
        redacted = Regex.Replace(redacted, "(password|passwd|pwd|token|authorization|cookie|secret|pin|otp)([\"'\\s:=]+)([^\"'\\s,;<>]{3,})", "$1$2[redacted]", RegexOptions.IgnoreCase);
        redacted = Regex.Replace(redacted, "[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{10,}", "[redacted-jwt]");
        redacted = Regex.Replace(redacted, "\\b\\d{8,}\\b", match => MaskLongNumber(match.Value));
        return redacted;
    }

    private static string MaskLongNumber(string value)
    {
        if (value.Length <= 4)
        {
            return value;
        }

        return new string('*', Math.Max(0, value.Length - 4)) + value[^4..];
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        return needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForMatch(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8);
    }

    private static void WriteMarkdownSummary(string path, IReadOnlyList<SiteSurveyReport> reports, SurveyOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Bank DOM Survey");
        builder.AppendLine();
        builder.AppendLine($"Captured at: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Profile root: `{options.ProfileRoot}`");
        builder.AppendLine();
        builder.AppendLine("| Site | Accessible | Authenticated | Bank clicked | Form likely | Captcha/OTP |");
        builder.AppendLine("| ---- | ---------- | ------------- | ------------ | ----------- | ----------- |");

        foreach (var report in reports)
        {
            builder.AppendLine($"| {report.Site} | {YesNo(report.Accessible)} | {YesNo(report.AuthenticatedLikely)} | {YesNo(report.BankNavigationAttempted)} | {YesNo(report.BankFormLikely)} | {YesNo(report.CaptchaOrOtpBlocker)} |");
        }

        foreach (var report in reports)
        {
            builder.AppendLine();
            builder.AppendLine($"## {report.Site}");
            builder.AppendLine($"- Target: `{report.TargetUrl}`");
            builder.AppendLine($"- Profile: `{report.ProfileFolder}`");
            builder.AppendLine($"- After open URL: `{report.AfterOpen?.Url ?? "n/a"}`");
            builder.AppendLine($"- After bank URL: `{report.BankNavigationAfterUrl ?? "n/a"}`");
            builder.AppendLine($"- Bank navigation selector: `{report.BankNavigationSelector ?? "not found"}`");
            builder.AppendLine($"- Bank navigation text: `{report.BankNavigationText ?? "not found"}`");
            builder.AppendLine($"- Auth signals: {JoinOrNone(report.AfterOpen?.AuthenticatedSignals)}");
            builder.AppendLine($"- Bank containers: {JoinOrNone(report.BankContainerSelectors)}");
            builder.AppendLine($"- Submit selectors: {JoinOrNone(report.SubmitSelectors)}");
            builder.AppendLine($"- Failure selectors: {JoinOrNone(report.FailureSelectors)}");
            builder.AppendLine($"- Blockers: {JoinOrNone(report.Blockers)}");
            builder.AppendLine($"- Files: `{report.Site.ToLowerInvariant()}/report.json`, `{report.Site.ToLowerInvariant()}/01-after-open.*`" +
                (report.AfterBankNavigation is null ? string.Empty : $", `{report.Site.ToLowerInvariant()}/02-after-bank-nav.*`"));
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static string JoinOrNone(IEnumerable<string>? values)
    {
        var materialized = values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(8).ToArray() ?? [];
        return materialized.Length == 0 ? "none" : string.Join("; ", materialized);
    }

    private static string YesNo(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        _ => "unknown"
    };
}

internal sealed record SiteDefinition(string Name, string Url);

internal sealed class SurveyOptions
{
    public string RepoRoot { get; init; } = Directory.GetCurrentDirectory();

    public string ProfileRoot { get; init; } = Path.Combine(Directory.GetCurrentDirectory(), "bin", "Debug", "net8.0-windows", "Profiles");

    public string OutputRoot { get; init; } = Path.Combine(Directory.GetCurrentDirectory(), "diagnostics", "bank-runtime", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));

    public bool Headed { get; init; } = true;

    public int PageLoadTimeoutSeconds { get; init; } = 35;

    public IReadOnlyList<string> Sites { get; init; } = ["RR88", "MM88", "XX88"];

    public static SurveyOptions Parse(string[] args)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var profileRoot = Path.Combine(repoRoot, "bin", "Debug", "net8.0-windows", "Profiles");
        var outputRoot = Path.Combine(repoRoot, "diagnostics", "bank-runtime", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        var headed = true;
        var timeout = 35;
        IReadOnlyList<string> sites = ["RR88", "MM88", "XX88"];

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile-root" when index + 1 < args.Length:
                    profileRoot = Path.GetFullPath(args[++index]);
                    break;
                case "--output" when index + 1 < args.Length:
                    outputRoot = Path.GetFullPath(args[++index]);
                    break;
                case "--sites" when index + 1 < args.Length:
                    sites = args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--headless":
                    headed = false;
                    break;
                case "--timeout" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed):
                    timeout = parsed;
                    index++;
                    break;
            }
        }

        return new SurveyOptions
        {
            RepoRoot = repoRoot,
            ProfileRoot = profileRoot,
            OutputRoot = outputRoot,
            Headed = headed,
            PageLoadTimeoutSeconds = timeout,
            Sites = sites
        };
    }
}

internal sealed class SiteSurveyReport
{
    public string Site { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string? ProfileFolder { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool? Accessible { get; set; }
    public bool? AuthenticatedLikely { get; set; }
    public bool? CaptchaOrOtpBlocker { get; set; }
    public bool BankNavigationAttempted { get; set; }
    public string? BankNavigationSelector { get; set; }
    public string? BankNavigationText { get; set; }
    public string? BankNavigationBeforeUrl { get; set; }
    public string? BankNavigationAfterUrl { get; set; }
    public bool BankFormLikely { get; set; }
    public PageSnapshot? AfterOpen { get; set; }
    public PageSnapshot? AfterBankNavigation { get; set; }
    public List<string> AuthenticatedSignals { get; set; } = [];
    public List<string> BankContainerSelectors { get; set; } = [];
    public List<FieldCandidate> BankFormFields { get; set; } = [];
    public List<string> SubmitSelectors { get; set; } = [];
    public List<string> FailureSelectors { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}

internal sealed class PageSnapshot
{
    public string Stage { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string ScreenshotFile { get; set; } = string.Empty;
    public string ElementsFile { get; set; } = string.Empty;
    public IReadOnlyList<IframeInfo> Iframes { get; set; } = [];
    public List<FrameCapture> FrameFiles { get; set; } = [];
    public IReadOnlyList<UiElementInfo> Elements { get; set; } = [];
    public List<string> AuthenticatedSignals { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
    public bool AuthenticatedLikely { get; set; }
    public bool BlockerLikely { get; set; }

    public PageSnapshot WithoutElementPayload()
    {
        return new PageSnapshot
        {
            Stage = Stage,
            CapturedAt = CapturedAt,
            Url = Url,
            Title = Title,
            SourceFile = SourceFile,
            ScreenshotFile = ScreenshotFile,
            ElementsFile = ElementsFile,
            Iframes = Iframes,
            FrameFiles = FrameFiles,
            AuthenticatedSignals = AuthenticatedSignals,
            Blockers = Blockers,
            AuthenticatedLikely = AuthenticatedLikely,
            BlockerLikely = BlockerLikely,
            Elements = []
        };
    }
}

internal sealed class UiElementInfo
{
    public string? Tag { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Role { get; set; }
    public string? AriaLabel { get; set; }
    public string? Placeholder { get; set; }
    public string? ClassName { get; set; }
    public string? Href { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? CssPath { get; set; }
    public bool Visible { get; set; }
    public bool Enabled { get; set; }
    public Dictionary<string, string> Data { get; set; } = [];

    [JsonIgnore]
    public string SafeText => ProgramText.TrimForReport(Text);
}

internal static class ProgramText
{
    public static string TrimForReport(string? value)
    {
        var normalized = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }
}

internal sealed class IframeInfo
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Src { get; set; }
    public string? Title { get; set; }
    public string? ClassName { get; set; }
    public bool Visible { get; set; }
}

internal sealed class FrameCapture
{
    public int Index { get; set; }
    public string? Src { get; set; }
    public string? SourceFile { get; set; }
    public string? ElementsFile { get; set; }
    public string? Error { get; set; }
}

internal sealed class FieldCandidate
{
    public string? CssPath { get; set; }
    public string? Tag { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Placeholder { get; set; }
    public string? AriaLabel { get; set; }
    public string? LikelyPurpose { get; set; }
}
