using System.Diagnostics;
using System.Text.Json;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Strategies;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

const string loginUrl = "https://gioithieu.rr3311.com/m/login";
const string bankUrl = "https://gioithieu.rr3311.com/m/securityCenter/addBankCard?type=B";

var successFile = args.Length > 0
    ? Path.GetFullPath(args[0])
    : FindLatestBenchmarkSuccessFile();
var navigationMode = args.Length > 1
    ? args[1].Trim().ToLowerInvariant()
    : "full";

var credential = ReadLatestRr88Credential(successFile);
var profilePath = Path.Combine(
    AppContext.BaseDirectory,
    "profile",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profilePath);

var options = new ChromeOptions
{
    PageLoadStrategy = PageLoadStrategy.None,
    UnhandledPromptBehavior = UnhandledPromptBehavior.Accept
};
options.AddArgument("--headless=new");
options.AddArgument("--window-size=800,600");
options.AddArgument($"--user-data-dir={profilePath}");
options.AddArgument("--disable-background-networking");
options.AddArgument("--disable-default-apps");
options.AddArgument("--disable-sync");
options.AddArgument("--disable-extensions");
options.SetLoggingPreference(LogType.Performance, LogLevel.All);

var service = ChromeDriverService.CreateDefaultService();
service.HideCommandPromptWindow = true;
service.SuppressInitialDiagnosticInformation = true;

using var driver = new ChromeDriver(service, options);
driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);

var stopwatch = Stopwatch.StartNew();
Console.WriteLine(
    $"DIAGNOSTIC START CredentialSource={successFile}; UsernameRedacted=true; NavigationMode={navigationMode}");

driver.Navigate().GoToUrl(loginUrl);
var wait = CreateWait(driver, TimeSpan.FromSeconds(30));
var usernameInput = wait.Until(webDriver =>
    FindVisible(webDriver, By.CssSelector("input[name='username']")))
    ?? throw new WebDriverTimeoutException("Could not find the RR88 username input.");
var passwordInput = wait.Until(webDriver =>
    FindVisible(webDriver, By.CssSelector("input[name='password']")))
    ?? throw new WebDriverTimeoutException("Could not find the RR88 password input.");

SetInputValue(driver, usernameInput, credential.Username);
SetInputValue(driver, passwordInput, credential.Password);

PrintLoginCandidateShape(driver);
var loginButton = wait.Until(webDriver =>
    FindBestLoginButton(webDriver));
if (loginButton is null)
{
    throw new WebDriverTimeoutException("Could not find the RR88 member login button.");
}

((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", loginButton);
wait.Until(webDriver =>
{
    var currentUrl = SafeUrl(webDriver);
    return !currentUrl.Contains("/m/login", StringComparison.OrdinalIgnoreCase)
           && StorageContainsAuthenticatedSession(webDriver);
});

Console.WriteLine(
    $"LOGIN READY ElapsedMs={stopwatch.ElapsedMilliseconds}; Url={SanitizeUrl(SafeUrl(driver))}");
PrintStorageShape(driver);
PrintRelevantGlobals(driver);

driver.Manage().Logs.GetLog(LogType.Performance);
var bankStart = stopwatch.ElapsedMilliseconds;

if (navigationMode == "workflow-idempotency")
{
    var account = new AccountModel
    {
        Username = credential.Username,
        Password = credential.Password,
        FullName = credential.FullName,
        AccountNumber = credential.AccountNumber,
        Level2Password = credential.Level2Password,
        BankName = "VIETCOMBANK",
        Branch = "HA NOI"
    };
    var siteRun = account.SiteRuns.Single(run => run.Site == TargetSite.RR88);
    siteRun.IsSelected = true;
    using var pauseEvent = new ManualResetEventSlim(initialState: true);
    var executionContext = new AutomationExecutionContext(
        CancellationToken.None,
        pauseEvent);
    var statusHandler = new EventHandler<AutomationStatusChangedEventArgs>((_, eventArgs) =>
    {
        if (ReferenceEquals(eventArgs.Account, account)
            && !string.IsNullOrWhiteSpace(eventArgs.Message))
        {
            Console.WriteLine(
                $"WORKFLOW STATUS {eventArgs.Status?.ToString() ?? "-"} {eventArgs.Message}");
        }
    });
    AutomationUi.StatusChanged += statusHandler;
    var workflowSucceeded = new RR88Strategy().ExecuteBankSetup(
        driver,
        account,
        siteRun,
        executionContext);
    AutomationUi.StatusChanged -= statusHandler;

    Console.WriteLine(
        $"WORKFLOW IDEMPOTENCY Success={workflowSucceeded}; " +
        $"ElapsedMs={stopwatch.ElapsedMilliseconds - bankStart}; " +
        $"FinalStatus={siteRun.Status}; SensitiveValuesPrinted=false");
    PrintNetworkTrace(driver, bankStart, stopwatch.ElapsedMilliseconds);
    Console.WriteLine(
        $"DIAGNOSTIC END ElapsedMs={stopwatch.ElapsedMilliseconds}; PersistentDataChanged=false");
    Environment.ExitCode = workflowSucceeded ? 0 : 1;
    return;
}

if (navigationMode == "card-inspect")
{
    var cardShape = InspectBankCards(driver, credential.AccountNumber);
    Console.WriteLine(
        $"BANK CARD INSPECT Shape={JsonSerializer.Serialize(cardShape)}; " +
        "SensitiveValuesPrinted=false");
    PrintNetworkTrace(driver, bankStart, stopwatch.ElapsedMilliseconds);
    Console.WriteLine(
        $"DIAGNOSTIC END ElapsedMs={stopwatch.ElapsedMilliseconds}; PersistentDataChanged=false");
    return;
}

if (navigationMode is "store-preload" or "store-bind-existing")
{
    var preloadShape = navigationMode == "store-bind-existing"
        ? RunStoreBindExisting(driver, credential)
        : RunStorePreload(driver);
    Console.WriteLine(
        $"STORE DIRECT Mode={navigationMode}; ElapsedMs={stopwatch.ElapsedMilliseconds - bankStart}; " +
        $"Shape={JsonSerializer.Serialize(preloadShape)}");
    PrintMemberCenterStoreShape(driver);
    PrintNetworkTrace(driver, bankStart, stopwatch.ElapsedMilliseconds);
    Console.WriteLine(
        $"DIAGNOSTIC END ElapsedMs={stopwatch.ElapsedMilliseconds}; PersistentDataChanged=false");
    return;
}

if (navigationMode is "spa-stop" or "warmup" or "capture-bind")
{
    ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        if (arguments[1]) {
            try { window.stop(); } catch {}
        }
        const url = new URL(arguments[0]);
        window.history.pushState({}, '', url.pathname + url.search);
        window.dispatchEvent(new PopStateEvent('popstate', { state: window.history.state }));
        """,
        bankUrl,
        navigationMode is "spa-stop" or "capture-bind");
}
else if (navigationMode == "spa")
{
    ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const url = new URL(arguments[0]);
        window.history.pushState({}, '', url.pathname + url.search);
        window.dispatchEvent(new PopStateEvent('popstate', { state: window.history.state }));
        """,
        bankUrl);
}
else
{
    ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
        """
        const url = arguments[0];
        const done = arguments[arguments.length - 1];
        setTimeout(() => window.location.replace(url), 0);
        done(true);
        """,
        bankUrl);
}

var bankReady = false;
try
{
    CreateWait(driver, TimeSpan.FromSeconds(30)).Until(webDriver =>
        FindVisible(
            webDriver,
            By.CssSelector(".select-bank-type .inputCon, .bank-card-container, .type-group-area")) is not null);
    bankReady = true;
}
catch (WebDriverTimeoutException)
{
}

var bankReadyElapsed = stopwatch.ElapsedMilliseconds - bankStart;
Console.WriteLine(
    $"BANK ROUTE Ready={bankReady}; ElapsedMs={bankReadyElapsed}; Url={SanitizeUrl(SafeUrl(driver))}");

var dropdownReady = false;
try
{
    CreateWait(driver, TimeSpan.FromSeconds(10)).Until(webDriver =>
        FindVisible(webDriver, By.CssSelector(".select-bank-type .inputCon")) is not null);
    dropdownReady = true;
}
catch (WebDriverTimeoutException)
{
}

Console.WriteLine(
    $"BANK DROPDOWN Ready={dropdownReady}; ElapsedMs={stopwatch.ElapsedMilliseconds - bankStart}");
PrintMemberCenterStoreShape(driver);

await Task.Delay(1500);
if (navigationMode == "capture-bind")
{
    await SubmitExistingBankDataAsync(driver, wait, credential);
    await Task.Delay(2500);
}

PrintNetworkTrace(driver, bankStart, stopwatch.ElapsedMilliseconds);

Console.WriteLine(
    $"DIAGNOSTIC END ElapsedMs={stopwatch.ElapsedMilliseconds}; PersistentDataChanged=false");

static string FindLatestBenchmarkSuccessFile()
{
    var repositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    var candidates = Directory
        .EnumerateFiles(repositoryRoot, "success.txt", SearchOption.AllDirectories)
        .Where(path => path.Contains(
            $"{Path.DirectorySeparatorChar}LiveRegistrationBenchmark{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .ToArray();

    return candidates.FirstOrDefault()
           ?? throw new FileNotFoundException(
               "No LiveRegistrationBenchmark success.txt file was found. Pass its path as the first argument.");
}

static Credential ReadLatestRr88Credential(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Credential source file was not found.", path);
    }

    foreach (var line in File.ReadLines(path).Reverse())
    {
        var fields = line.Split('|');
        if (fields.Length >= 3
            && fields[0].Equals("RR88", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(fields[1])
            && !string.IsNullOrWhiteSpace(fields[2])
            && fields.Length >= 6)
        {
            return new Credential(
                fields[1].Trim(),
                fields[2].Trim(),
                fields[3].Trim(),
                fields[4].Trim(),
                fields[5].Trim());
        }
    }

    throw new InvalidDataException("No RR88 credential was found in the supplied success file.");
}

static WebDriverWait CreateWait(IWebDriver driver, TimeSpan timeout)
{
    var wait = new WebDriverWait(driver, timeout)
    {
        PollingInterval = TimeSpan.FromMilliseconds(200)
    };
    wait.IgnoreExceptionTypes(
        typeof(NoSuchElementException),
        typeof(StaleElementReferenceException),
        typeof(ElementNotInteractableException),
        typeof(WebDriverException));
    return wait;
}

static IWebElement? FindVisible(IWebDriver driver, By selector)
{
    foreach (var element in driver.FindElements(selector))
    {
        if (IsVisibleAndEnabled(element))
        {
            return element;
        }
    }

    return null;
}

static bool IsVisibleAndEnabled(IWebElement element)
{
    try
    {
        return element.Displayed && element.Enabled;
    }
    catch (WebDriverException)
    {
        return false;
    }
}

static void SetInputValue(IWebDriver driver, IWebElement input, string value)
{
    ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const element = arguments[0];
        const value = arguments[1];
        const setter = Object.getOwnPropertyDescriptor(
            window.HTMLInputElement.prototype,
            'value').set;
        setter.call(element, value);
        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
        element.dispatchEvent(new Event('blur', { bubbles: true }));
        """,
        input,
        value);
}

static IWebElement? FindBestLoginButton(IWebDriver driver)
{
    var candidates = driver.FindElements(By.CssSelector(
        "input[type='button'], input[type='submit'], button, [role='button']"));

    foreach (var element in candidates)
    {
        if (!IsVisibleAndEnabled(element))
        {
            continue;
        }

        var label = string.Join(
                " ",
                element.Text,
                element.GetAttribute("value"),
                element.GetAttribute("aria-label"),
                element.GetAttribute("class"))
            .ToLowerInvariant();

        if (label.Contains("login")
            || label.Contains("log in")
            || label.Contains("đăng nhập")
            || label.Contains("dang nhap")
            || label.Contains("登入")
            || label.Contains("btn-primary")
            || label.Contains("submit"))
        {
            return element;
        }
    }

    return null;
}

static void PrintLoginCandidateShape(IWebDriver driver)
{
    var shape = ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        return [...document.querySelectorAll(
            "input[type='button'], input[type='submit'], button, [role='button']")]
            .filter(element => element.offsetWidth && element.offsetHeight)
            .map(element => ({
                tag: element.tagName,
                type: element.getAttribute('type') || '',
                className: String(element.className || '').slice(0, 120),
                label: String(
                    element.innerText
                    || element.value
                    || element.getAttribute('aria-label')
                    || '').slice(0, 80)
            }));
        """);

    Console.WriteLine($"LOGIN CANDIDATES {JsonSerializer.Serialize(shape)}");
}

static bool StorageContainsAuthenticatedSession(IWebDriver driver)
{
    try
    {
        var result = ((IJavaScriptExecutor)driver).ExecuteScript(
            """
            const keys = [
                ...Object.keys(window.localStorage),
                ...Object.keys(window.sessionStorage)
            ];
            return keys.some(key =>
                /session|auth|token|member/i.test(key)
                && !/language/i.test(key));
            """);
        return result is bool authenticated && authenticated;
    }
    catch (WebDriverException)
    {
        return false;
    }
}

static void PrintStorageShape(IWebDriver driver)
{
    var shape = ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const describe = storage => Object.keys(storage).sort().map(key => {
            const value = storage.getItem(key) || '';
            let kind = 'text';
            try {
                const parsed = JSON.parse(value);
                kind = Array.isArray(parsed) ? 'array' : typeof parsed;
            } catch {}
            return { key, kind, length: value.length };
        });
        return {
            local: describe(window.localStorage),
            session: describe(window.sessionStorage)
        };
        """);

    Console.WriteLine($"STORAGE SHAPE {JsonSerializer.Serialize(shape)}");
}

static void PrintRelevantGlobals(IWebDriver driver)
{
    var globals = ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        return {
            reRsaV2: typeof window.reRsaV2,
            CryptoJS: typeof window.CryptoJS,
            tcgmodulemc: typeof window.tcgmodulemc,
            tcgmodulemcKeys: window.tcgmodulemc
                ? Object.keys(window.tcgmodulemc).slice(0, 40)
                : [],
            tcgmodulemcDefault: window.tcgmodulemc && window.tcgmodulemc.default
                ? {
                    type: typeof window.tcgmodulemc.default,
                    name: window.tcgmodulemc.default.name || '',
                    keys: Object.keys(window.tcgmodulemc.default).slice(0, 60),
                    prototypeKeys: window.tcgmodulemc.default.prototype
                        ? Object.getOwnPropertyNames(window.tcgmodulemc.default.prototype).slice(0, 60)
                        : []
                }
                : null,
            webpackJsonptcgmodulemc: typeof window.webpackJsonptcgmodulemc,
            webpackJsonp: typeof window.webpackJsonp
        };
        """);
    Console.WriteLine($"GLOBALS {JsonSerializer.Serialize(globals)}");
}

static object? RunStorePreload(IWebDriver driver)
{
    return ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
        """
        const done = arguments[arguments.length - 1];
        (async () => {
            const root = window.tcgmodulemc
                && window.tcgmodulemc.default
                && window.tcgmodulemc.default.memberCenterStore;
            if (!root || !root.seculist) {
                done({ ready: false, reason: 'store-unavailable' });
                return;
            }

            const call = (owner, method) => {
                if (!owner || typeof owner[method] !== 'function') {
                    return Promise.resolve({
                        method,
                        status: 'unavailable'
                    });
                }

                return Promise.resolve(owner[method]())
                    .then(value => ({
                        method,
                        status: 'fulfilled',
                        resultType: Array.isArray(value) ? 'array' : typeof value,
                        resultKeys: value && typeof value === 'object'
                            ? Object.keys(value).slice(0, 40)
                            : []
                    }))
                    .catch(error => ({
                        method,
                        status: 'rejected',
                        errorType: error && error.constructor
                            ? error.constructor.name
                            : typeof error
                    }));
            };

            const calls = await Promise.all([
                call(root.seculist, 'getBankLis'),
                call(root.seculist, 'bindConfig'),
                call(root.mcCommon, 'getMemberInfo'),
                call(root.mcCommon, 'getProvinces')
            ]);

            const bankList = Array.isArray(root.seculist.bankList)
                ? root.seculist.bankList
                : [];
            const matchedBank = bankList.find(item => {
                const text = JSON.stringify(item || {}).toLowerCase();
                return text.includes('vietcom');
            });
            let bankTemplate = null;
            if (matchedBank && typeof root.seculist.getBankTemplate === 'function') {
                try {
                    bankTemplate = await root.seculist.getBankTemplate(matchedBank.bankCode);
                } catch {}
            }

            done({
                ready: true,
                calls,
                bankListCount: bankList.length,
                bankItemKeys: bankList[0] ? Object.keys(bankList[0]).slice(0, 50) : [],
                matchedBankKeys: matchedBank ? Object.keys(matchedBank).slice(0, 50) : [],
                matchedBankRequirements: matchedBank ? {
                    province: matchedBank.isProvinceRequired,
                    city: matchedBank.isCityRequired,
                    branch: matchedBank.isBranchRequired,
                    playerId: matchedBank.isPlayerIdRequired,
                    playerEmail: matchedBank.isPlayerEmailRequired,
                    playerPhone: matchedBank.isPlayerPhoneRequired,
                    customize: matchedBank.isCustomizeRequired
                } : null,
                bankTemplateKeys: bankTemplate
                    ? Object.keys(bankTemplate).slice(0, 50)
                    : [],
                bankTemplateSettings: bankTemplate
                    && Array.isArray(bankTemplate.bankCardTemplateSettings)
                    ? bankTemplate.bankCardTemplateSettings.slice(0, 30).map(item => ({
                        cardItem: item.cardItem,
                        type: item.type,
                        isLegalNameSeparate: item.isLegalNameSeparate,
                        thirdPartyParamName: item.thirdPartyParamName
                    }))
                    : [],
                securityListV2Keys: root.seculist.securityListV2
                    ? Object.keys(root.seculist.securityListV2).slice(0, 50)
                    : [],
                memberInfoKeys: root.mcCommon && root.mcCommon.memberInfo
                    ? Object.keys(root.mcCommon.memberInfo).slice(0, 50)
                    : [],
                provinceCount: root.mcCommon && Array.isArray(root.mcCommon.provinces)
                    ? root.mcCommon.provinces.length
                    : 0,
                provinceItemKeys: root.mcCommon
                    && Array.isArray(root.mcCommon.provinces)
                    && root.mcCommon.provinces[0]
                        ? Object.keys(root.mcCommon.provinces[0]).slice(0, 50)
                        : []
            });
        })().catch(error => done({
            ready: false,
            reason: error && error.constructor
                ? error.constructor.name
                : typeof error
        }));
        """);
}

static object? InspectBankCards(IWebDriver driver, string accountNumber)
{
    return ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
        """
        const accountNumber = String(arguments[0] || '');
        const done = arguments[arguments.length - 1];
        const root = window.tcgmodulemc
            && window.tcgmodulemc.default
            && window.tcgmodulemc.default.memberCenterStore;
        const withdraw = root && root.withdraw;
        if (!withdraw || typeof withdraw.getBankCard !== 'function') {
            done({ ready: false, reason: 'method-unavailable' });
            return;
        }

        Promise.resolve(withdraw.getBankCard())
            .then(response => {
                const value = response && response.value !== undefined
                    ? response.value
                    : response && response.data && response.data.value !== undefined
                        ? response.data.value
                        : withdraw.bankCard;
                const list = Array.isArray(value)
                    ? value
                    : value && Array.isArray(value.list)
                        ? value.list
                        : value && Array.isArray(value.data)
                            ? value.data
                            : [];
                const collections = {};
                for (const key of [
                    'bankCards',
                    'fullBankCardList',
                    'fullSortedCardList',
                    'cardNetworkCards',
                    'virtualWallets',
                    'eWallets'
                ]) {
                    const candidate = value && Array.isArray(value[key])
                        ? value[key]
                        : [];
                    collections[key] = {
                        count: candidate.length,
                        itemKeys: candidate[0] && typeof candidate[0] === 'object'
                            ? Object.keys(candidate[0]).slice(0, 40)
                            : []
                    };
                }
                const firstBankCard = value
                    && Array.isArray(value.bankCards)
                    && value.bankCards.length > 0
                    ? value.bankCards[0]
                    : null;
                const cardNumberText = firstBankCard
                    ? String(firstBankCard.cardNumber || '')
                    : '';
                const serialized = JSON.stringify(value || '');
                const expectedSuffix = accountNumber.length >= 4
                    ? accountNumber.slice(-4)
                    : accountNumber;
                const suffixMatch = [
                    value && value.bankCards,
                    value && value.fullBankCardList,
                    value && value.fullSortedCardList
                ]
                    .filter(Array.isArray)
                    .flat()
                    .some(card => {
                        const digits = String(card && card.cardNumber || '')
                            .replace(/\D/g, '');
                        return digits.length >= expectedSuffix.length
                            && digits.endsWith(expectedSuffix);
                    });
                done({
                    ready: true,
                    responseSuccess: Boolean(response && response.success),
                    responseStatus: response && response.status
                        ? response.status
                        : null,
                    valueType: Array.isArray(value) ? 'array' : typeof value,
                    valueKeys: value && typeof value === 'object'
                        ? Object.keys(value).slice(0, 40)
                        : [],
                    count: list.length,
                    itemKeys: list[0] && typeof list[0] === 'object'
                        ? Object.keys(list[0]).slice(0, 40)
                        : [],
                    collections,
                    firstCardNumberShape: {
                        type: typeof (firstBankCard && firstBankCard.cardNumber),
                        length: cardNumberText.length,
                        masked: cardNumberText.includes('*'),
                        digitCount: (cardNumberText.match(/\d/g) || []).length
                    },
                    accountMatch: Boolean(
                        accountNumber
                        && (serialized.includes(accountNumber) || suffixMatch)),
                });
            })
            .catch(error => done({
                ready: false,
                reason: error && error.constructor
                    ? error.constructor.name
                    : typeof error
            }));
        """,
        accountNumber);
}

static object? RunStoreBindExisting(IWebDriver driver, Credential credential)
{
    return ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
        """
        const accountNumber = arguments[0];
        const paymentPassword = arguments[1];
        const suppliedPayee = arguments[2];
        const branch = arguments[3];
        const done = arguments[arguments.length - 1];

        (async () => {
            const root = window.tcgmodulemc
                && window.tcgmodulemc.default
                && window.tcgmodulemc.default.memberCenterStore;
            const seculist = root && root.seculist;
            if (!seculist
                || typeof seculist.getBankLis !== 'function'
                || typeof seculist.bindConfig !== 'function'
                || typeof seculist.bindCard !== 'function') {
                done({ ready: false, reason: 'store-method-unavailable' });
                return;
            }

            await Promise.all([
                seculist.getBankLis(),
                seculist.bindConfig(),
                root.mcCommon && typeof root.mcCommon.getMemberInfo === 'function'
                    ? root.mcCommon.getMemberInfo()
                    : Promise.resolve()
            ]);

            const compact = value => String(value || '')
                .normalize('NFD')
                .replace(/[\u0300-\u036f]/g, '')
                .toLowerCase()
                .replace(/[^a-z0-9]/g, '');
            const bank = (seculist.bankList || []).find(item => {
                const candidate = compact(
                    `${item.bankEngName || ''} ${item.bankLocaleName || ''} ${item.bankCode || ''}`);
                return candidate.includes('vietcombank')
                    || candidate.includes('vietcom');
            });
            if (!bank) {
                done({ ready: false, reason: 'bank-not-found' });
                return;
            }

            const memberInfo = root.mcCommon && root.mcCommon.memberInfo
                ? root.mcCommon.memberInfo
                : {};
            const payload = {
                area: branch || '',
                paymentPassword,
                validatePaymentPassword: paymentPassword,
                verificationCode: '',
                firebaseIdToken: '',
                mobileNum: memberInfo.mobile || '',
                cardType: 'B',
                countryDialingCode: memberInfo.countryDialingCode || '',
                playerId: '',
                customField: '',
                email: '',
                mobileNo: '',
                bankName: bank.bankEngName || bank.bankLocaleName || '',
                bankCode: bank.bankCode || '',
                payeeName: memberInfo.payee || suppliedPayee,
                cardNumber: accountNumber
            };

            try {
                const response = await seculist.bindCard(payload);
                const data = response && response.data;
                done({
                    ready: true,
                    status: response && response.status,
                    resultType: Array.isArray(response) ? 'array' : typeof response,
                    resultKeys: response && typeof response === 'object'
                        ? Object.keys(response).slice(0, 40)
                        : [],
                    dataType: Array.isArray(data) ? 'array' : typeof data,
                    dataKeys: data && typeof data === 'object'
                        ? Object.keys(data).slice(0, 40)
                        : [],
                    success: Boolean(
                        (data && data.success === true)
                        || (response && response.success === true))
                });
            } catch (error) {
                done({
                    ready: true,
                    rejected: true,
                    errorType: error && error.constructor
                        ? error.constructor.name
                        : typeof error,
                    status: error && error.response
                        ? error.response.status
                        : null,
                    dataKeys: error && error.response && error.response.data
                        && typeof error.response.data === 'object'
                        ? Object.keys(error.response.data).slice(0, 40)
                        : []
                });
            }
        })().catch(error => done({
            ready: false,
            reason: error && error.constructor
                ? error.constructor.name
                : typeof error
        }));
        """,
        credential.AccountNumber,
        credential.Level2Password,
        credential.FullName,
        "HA NOI");
}

static void PrintMemberCenterStoreShape(IWebDriver driver)
{
    var shape = ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const summarize = value => {
            if (!value) return null;
            const prototype = Object.getPrototypeOf(value);
            const ownPropertyNames = Object.getOwnPropertyNames(value);
            const functionProperties = ownPropertyNames
                .filter(key => {
                    try { return typeof value[key] === 'function'; }
                    catch { return false; }
                });
            return {
                type: typeof value,
                keys: Object.keys(value).slice(0, 120),
                ownPropertyNames: ownPropertyNames.slice(0, 160),
                functionProperties: functionProperties.slice(0, 120),
                prototypeKeys: prototype
                    ? Object.getOwnPropertyNames(prototype).slice(0, 120)
                    : []
            };
        };

        const root = window.tcgmodulemc
            && window.tcgmodulemc.default
            && window.tcgmodulemc.default.memberCenterStore;
        if (!root) return null;

        const nested = {};
        for (const key of Object.keys(root).slice(0, 80)) {
            const value = root[key];
            if (value && (typeof value === 'object' || typeof value === 'function')) {
                nested[key] = summarize(value);
            }
        }

        const seculist = root.seculist;
        const seculistNested = {};
        if (seculist) {
            for (const key of [
                'SecurityList',
                'securityListV2',
                'bankCardList',
                'bankList',
                'paymentData',
                'test',
                'getBankLis',
                'bindConfig',
                'bindCard'
            ]) {
                if (seculist[key]) {
                    seculistNested[key] = summarize(seculist[key]);
                }
            }
        }

        return { root: summarize(root), nested, seculistNested };
        """);

    Console.WriteLine($"MEMBER STORE SHAPE {JsonSerializer.Serialize(shape)}");
}

static void PrintNetworkTrace(
    IWebDriver driver,
    long bankStartMilliseconds,
    long traceEndMilliseconds)
{
    var requests = new Dictionary<string, NetworkRequest>(StringComparer.Ordinal);

    foreach (var entry in driver.Manage().Logs.GetLog(LogType.Performance))
    {
        using var document = JsonDocument.Parse(entry.Message);
        var message = document.RootElement.GetProperty("message");
        var method = message.GetProperty("method").GetString();
        var parameters = message.GetProperty("params");

        if (method == "Network.requestWillBeSent")
        {
            var request = parameters.GetProperty("request");
            var url = request.GetProperty("url").GetString() ?? string.Empty;
            if (!IsRelevantNetworkUrl(url))
            {
                continue;
            }

            var requestId = parameters.GetProperty("requestId").GetString() ?? string.Empty;
            var requestTimestamp = parameters.TryGetProperty("timestamp", out var requestTimestampElement)
                ? requestTimestampElement.GetDouble()
                : (double?)null;
            var headers = request.TryGetProperty("headers", out var headerObject)
                ? headerObject.EnumerateObject().Select(property => property.Name).Order().ToArray()
                : [];

            requests[requestId] = new NetworkRequest
            {
                Method = request.GetProperty("method").GetString() ?? string.Empty,
                Url = SanitizeUrl(url),
                HeaderNames = headers,
                HasPostData = request.TryGetProperty("hasPostData", out var hasPostData)
                              && hasPostData.ValueKind == JsonValueKind.True,
                PostDataLength = request.TryGetProperty("postData", out var postData)
                                 && postData.ValueKind == JsonValueKind.String
                    ? postData.GetString()?.Length
                    : null,
                RequestTimestamp = requestTimestamp
            };
        }
        else if (method == "Network.responseReceived")
        {
            var requestId = parameters.GetProperty("requestId").GetString() ?? string.Empty;
            if (!requests.TryGetValue(requestId, out var request))
            {
                continue;
            }

            var response = parameters.GetProperty("response");
            request.Status = response.GetProperty("status").GetInt32();
            request.ResponseTimestamp = parameters.TryGetProperty("timestamp", out var responseTimestampElement)
                ? responseTimestampElement.GetDouble()
                : null;
            request.FromDiskCache = response.TryGetProperty("fromDiskCache", out var diskCache)
                                    && diskCache.ValueKind == JsonValueKind.True;
            request.FromServiceWorker = response.TryGetProperty("fromServiceWorker", out var worker)
                                        && worker.ValueKind == JsonValueKind.True;
        }
    }

    Console.WriteLine(
        $"NETWORK TRACE WindowMs={traceEndMilliseconds - bankStartMilliseconds}; Requests={requests.Count}");
    foreach (var request in requests.Values.OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(
            $"NETWORK {request.Method} {request.Status?.ToString() ?? "-"} {request.Url}; " +
            $"PostData={request.HasPostData}; DiskCache={request.FromDiskCache}; " +
            $"PostDataLength={request.PostDataLength?.ToString() ?? "-"}; " +
            $"ServiceWorker={request.FromServiceWorker}; DurationMs={request.DurationMs:0}; " +
            $"Headers={string.Join(',', request.HeaderNames)}");
    }
}

static bool IsRelevantNetworkUrl(string url) =>
    url.Contains("/wps/", StringComparison.OrdinalIgnoreCase)
    || url.Contains("addBankCard", StringComparison.OrdinalIgnoreCase)
    || url.Contains("/m/app.", StringComparison.OrdinalIgnoreCase)
    || url.Contains("/m/chunk-common.", StringComparison.OrdinalIgnoreCase);

static async Task SubmitExistingBankDataAsync(
    IWebDriver driver,
    WebDriverWait wait,
    Credential credential)
{
    var dropdown = wait.Until(webDriver =>
        FindVisible(webDriver, By.CssSelector(".select-bank-type .inputCon")))
        ?? throw new WebDriverTimeoutException("Bank selector was not available for capture.");
    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", dropdown);

    var bankItem = wait.Until(webDriver =>
        webDriver.FindElements(By.CssSelector(".popUps-list-item"))
            .FirstOrDefault(element =>
                IsVisibleAndEnabled(element)
                && Compact(element.Text).Contains("vietcom", StringComparison.OrdinalIgnoreCase)));
    if (bankItem is null)
    {
        throw new WebDriverTimeoutException("VIETCOM BANK was not available for capture.");
    }

    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", bankItem);
    _ = wait.Until(webDriver =>
        FindVisible(webDriver, By.CssSelector("input[name='bankCard']")))
        ?? throw new WebDriverTimeoutException("Bank account input was not rendered after selecting the bank.");

    ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const setValue = (element, value) => {
            const setter = Object.getOwnPropertyDescriptor(
                window.HTMLInputElement.prototype, 'value').set;
            setter.call(element, value);
            element.dispatchEvent(new Event('input', { bubbles: true }));
            element.dispatchEvent(new Event('change', { bubbles: true }));
            element.dispatchEvent(new Event('blur', { bubbles: true }));
        };

        const bankCard = document.querySelector('input[name="bankCard"]');
        const branch = document.querySelector('input[name="customBankBranch"]');
        if (bankCard) setValue(bankCard, arguments[0]);
        if (branch) setValue(branch, 'HA NOI');

        const passwords = [...document.querySelectorAll('input[type="password"]')]
            .filter(element => element.offsetWidth && element.offsetHeight);
        if (passwords.length >= 2) {
            setValue(passwords[passwords.length - 2], arguments[1]);
            setValue(passwords[passwords.length - 1], arguments[1]);
        }
        """,
        credential.AccountNumber,
        credential.Level2Password);

    PrintBankFormShape(driver);
    await Task.Delay(350);
    ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const normalize = value => (value || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .toLowerCase();
        const buttons = [...document.querySelectorAll('.am-button, button, [role="button"]')]
            .filter(element => element.offsetWidth && element.offsetHeight);
        const button = buttons.find(element => {
            const text = normalize(element.innerText || element.textContent);
            return text.includes('xac nhan')
                || text.includes('luu')
                || text.includes('confirm')
                || text.includes('save')
                || element.className.toString().includes('btn-success');
        });
        if (button) button.click();
        """);

    await Task.Delay(500);
    ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const normalize = value => (value || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .toLowerCase();
        const buttons = [...document.querySelectorAll(
            '.am-modal-button, .am-dialog-button, .dialog-btn, ' +
            '.van-dialog__confirm, .am-modal button, .am-dialog button, ' +
            '.van-dialog button, .dialog button')]
            .filter(element => element.offsetWidth && element.offsetHeight);
        const button = buttons.find(element => {
            const text = normalize(element.innerText || element.textContent);
            return text.includes('xac nhan')
                || text.includes('confirm')
                || text.includes('dong y')
                || text === 'ok';
        }) || buttons[buttons.length - 1];
        if (button) button.click();
        """);

    Console.WriteLine(
        "BIND CAPTURE SubmitClicked=true; ConfirmationAttempted=true; SensitiveValuesPrinted=false");
}

static void PrintBankFormShape(IWebDriver driver)
{
    var shape = ((IJavaScriptExecutor)driver).ExecuteScript(
        """
        const visible = element => element.offsetWidth && element.offsetHeight;
        return {
            inputs: [...document.querySelectorAll('input')]
                .filter(visible)
                .map(element => ({
                    name: element.name || '',
                    type: element.type || '',
                    placeholder: element.placeholder || '',
                    className: String(element.className || '').slice(0, 100),
                    disabled: Boolean(element.disabled),
                    hasValue: Boolean(element.value)
                })),
            buttons: [...document.querySelectorAll('.am-button, button, [role="button"]')]
                .filter(visible)
                .map(element => ({
                    label: String(element.innerText || element.textContent || '').slice(0, 80),
                    className: String(element.className || '').slice(0, 120),
                    disabled: Boolean(element.disabled)
                }))
        };
        """);

    Console.WriteLine($"BANK FORM SHAPE {JsonSerializer.Serialize(shape)}");
}

static string Compact(string? value) =>
    string.Concat(
        (value ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(character =>
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit))
        .ToLowerInvariant();

static string SafeUrl(IWebDriver driver)
{
    try
    {
        return driver.Url ?? string.Empty;
    }
    catch (WebDriverException)
    {
        return string.Empty;
    }
}

static string SanitizeUrl(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return url.Split('?', 2)[0];
    }

    return uri.GetLeftPart(UriPartial.Path);
}

internal sealed record Credential(
    string Username,
    string Password,
    string FullName,
    string AccountNumber,
    string Level2Password);

internal sealed class NetworkRequest
{
    public string Method { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public IReadOnlyList<string> HeaderNames { get; init; } = [];

    public bool HasPostData { get; init; }

    public int? PostDataLength { get; init; }

    public int? Status { get; set; }

    public bool FromDiskCache { get; set; }

    public bool FromServiceWorker { get; set; }

    public double? RequestTimestamp { get; init; }

    public double? ResponseTimestamp { get; set; }

    public double? DurationMs =>
        RequestTimestamp.HasValue && ResponseTimestamp.HasValue
            ? (ResponseTimestamp.Value - RequestTimestamp.Value) * 1000
            : null;
}
