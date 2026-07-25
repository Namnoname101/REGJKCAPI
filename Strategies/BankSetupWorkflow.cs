using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

internal static class BankSetupWorkflow
{
    private static readonly By[] PopupCloseSelectors =
    [
        By.CssSelector(".am-navbar-title.close-btn"),
        By.CssSelector(".am-navbar-title"),
        By.CssSelector(".deposit-close"),
        By.CssSelector(".deposit-guide-close"),
        By.CssSelector(".modal-close"),
        By.CssSelector(".popup_modal_close"),
        By.CssSelector(".close"),
        By.CssSelector(".am-icon-cross"),
        By.CssSelector(".dialog-close"),
        By.CssSelector(".van-icon-cross"),
        By.CssSelector(".home-popup-close"),
        By.CssSelector(".modal-close-btn"),
        By.CssSelector(".close-btn"),
        By.CssSelector(".dialog-tips-close")
    ];

    private static readonly By[] FooterNavigationSelectors =
    [
        By.CssSelector("div.footer-menu-item:nth-child(5)"),
        By.CssSelector(".footer-menu-item:nth-child(5)"),
        By.CssSelector("a.footer-menu-item:last-child"),
        By.CssSelector("a.footer-item:last-child"),
        By.CssSelector(".footer-menu-item:last-child")
    ];

    private static readonly By[] SecondaryNavigationSelectors =
    [
        By.CssSelector("a.footer-item:nth-child(2)"),
        By.CssSelector(".default-member-footer a.footer-item:nth-child(2)"),
        By.CssSelector(".footer-item:nth-child(2)")
    ];

    private static readonly By[] AddBankSelectors =
    [
        By.CssSelector(".withdraw-bkadd"),
        By.CssSelector(".bank-add"),
        By.CssSelector(".add-bank"),
        By.CssSelector(".addCard"),
        By.CssSelector(".add-btn"),
        By.CssSelector(".van-icon-plus"),
        By.CssSelector("img[src*='add']"),
        By.CssSelector("div[class*='add']"),
        By.XPath("//*[contains(text(), 'Thêm') or contains(text(), '+')]")
    ];

    private static readonly By[] BankDropdownSelectors =
    [
        By.CssSelector(".select-bank-type .inputCon"),
        By.CssSelector(".inputCon")
    ];

    private static readonly By[] BankListItemSelectors =
    [
        By.CssSelector(".popUps-list-item")
    ];

    private static readonly By[] AccountNumberSelectors =
    [
        By.CssSelector("div.account-form-group:nth-child(2) > div:nth-child(1) > input:nth-child(2)"),
        By.CssSelector(".account-form-group:nth-child(2) input:nth-of-type(2)"),
        By.CssSelector(".account-form-group:nth-child(2) input")
    ];

    private static readonly By[] BranchSelectors =
    [
        By.CssSelector("div.account-form-group:nth-child(3) > div:nth-child(1) > input:nth-child(2)"),
        By.CssSelector(".account-form-group:nth-child(3) input:nth-of-type(2)"),
        By.CssSelector(".account-form-group:nth-child(3) input")
    ];

    private static readonly By[] PasswordSelectors =
    [
        By.CssSelector(".payment-password input"),
        By.CssSelector("div.mc-account-list:nth-child(2) > input:nth-child(2)"),
        By.CssSelector("div.account-form-group:nth-child(5) > div:nth-child(1) > input:nth-child(2)"),
        By.CssSelector("input[type='password']")
    ];

    private static readonly By[] SubmitSelectors =
    [
        By.CssSelector("span.am-button"),
        By.CssSelector(".am-button"),
        By.CssSelector("button")
    ];

    public static bool Execute(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext,
        string? directBankUrl = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);
        ArgumentNullException.ThrowIfNull(executionContext);

        var bankName = account.BankName.Trim();
        var bankAccount = account.AccountNumber.Trim();
        var branch = account.Branch.Trim();
        var withdrawPassword = account.Level2Password.Trim();

        if (string.IsNullOrWhiteSpace(bankName)
            || string.IsNullOrWhiteSpace(bankAccount)
            || string.IsNullOrWhiteSpace(withdrawPassword))
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Bank setup data missing. Required: Bank, Account Number, Level 2 Password.");
            return false;
        }

        try
        {
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Closing post-registration popups...");
            CloseOptionalPopups(driver, executionContext);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Navigating to bank setup...");

            try
            {
                if (driver is IJavaScriptExecutor js)
                {
                    js.ExecuteScript("document.querySelectorAll('.van-overlay, .toast_bg, .registerModal, .am-modal-mask, .dialog-mask').forEach(e => e.remove());");
                }
            }
            catch { }

            var fastBindResult = TryFastBindThroughMemberStore(
                driver,
                account,
                bankName,
                bankAccount,
                branch,
                withdrawPassword,
                executionContext);
            if (fastBindResult == FastBankBindResult.Success)
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    logMessage: "Bank setup succeeded through authenticated API path.");
                return true;
            }

            if (fastBindResult == FastBankBindResult.Rejected)
            {
                if (VerifyBankCardLinked(driver, bankAccount, bankName))
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        logMessage: "Bank was already linked; treating the idempotent API result as success.");
                    return true;
                }

                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    logMessage: "Authenticated API path was rejected; continuing with the visual fallback.");
            }
            else if (fastBindResult == FastBankBindResult.Indeterminate)
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Failed,
                    "Bank API submission could not be verified. Visual retry was skipped to prevent a duplicate bank binding.");
                return false;
            }

            NavigateToBankSetup(driver, executionContext, directBankUrl);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Opening bank selector...");
            if (string.IsNullOrWhiteSpace(directBankUrl))
            {
                // Only search for the add-bank button when direct navigation
                // was unavailable and the site returned to its home page.
                ClickAnyVisible(driver, AddBankSelectors, TimeSpan.FromSeconds(3), required: false);
            }
            ClickAnyVisible(driver, BankDropdownSelectors, TimeSpan.FromSeconds(3), required: true);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Selecting bank...");
            ClickBankName(driver, bankName, TimeSpan.FromSeconds(5));

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Filling bank details...");
            FillBankAccountAndBranch(driver, bankAccount, branch, executionContext);
            FillWithdrawPassword(driver, withdrawPassword, executionContext);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Submitting bank setup...");
            ClickSubmitButton(driver, TimeSpan.FromSeconds(15));

            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Handling confirmation popup if any...");
            ClickConfirmationPopupIfAny(driver, executionContext);

            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Waiting for bank binding result...");
            
            // Poll continually for up to 5 seconds to catch toasts instantly
            var pollEndTime = DateTime.Now.AddSeconds(5);
            if (driver is IJavaScriptExecutor jsChecker)
            {
                while (DateTime.Now < pollEndTime)
                {
                    if (executionContext.CancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var resultMessage = jsChecker.ExecuteScript(@"
                            var allEls = document.querySelectorAll('div, span, p, .van-toast__text, .van-dialog__message');
                            for(var i=0; i<allEls.length; i++) {
                                var el = allEls[i];
                                if (el.offsetWidth > 0 && el.offsetHeight > 0 && el.innerText && el.innerText.trim().length < 150) {
                                    var text = el.innerText.toLowerCase();
                                    
                                    // Bắt lỗi
                                    if (text.includes('sử dụng') || 
                                        text.includes('tồn tại') || 
                                        text.includes('lỗi') || 
                                        text.includes('thất bại') || 
                                        text.includes('trùng') || 
                                        text.includes('không đúng') || 
                                        text.includes('already') || 
                                        text.includes('vui lòng thử lại') ||
                                        text.includes('sai')) {
                                        return 'ERROR:' + el.innerText.trim();
                                    }
                                    
                                    // Bắt thành công
                                    if (text.includes('thành công') || text.includes('success')) {
                                        return 'SUCCESS:' + el.innerText.trim();
                                    }
                                }
                            }
                            return null;
                        ") as string;

                        if (!string.IsNullOrEmpty(resultMessage))
                        {
                            if (resultMessage.StartsWith("ERROR:"))
                            {
                                AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup failed: {resultMessage.Substring(6)}");
                                return false;
                            }
                            else if (resultMessage.StartsWith("SUCCESS:"))
                            {
                                AutomationUi.SetSiteStatus(account, siteRun, logMessage: $"Bank setup success: {resultMessage.Substring(8)}");
                                return true; // Trả về true liền ngay lập tức để qua con khác
                            }
                        }
                    }
                    catch { }

                    executionContext.Delay(TimeSpan.FromMilliseconds(500));
                }
            }

            if (VerifyBankCardLinked(driver, bankAccount, bankName))
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    logMessage: "Bank setup submitted and verified through the bound-card list.");
                return true;
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Bank setup returned no success message and the bound-card list did not confirm the account.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverTimeoutException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup timeout: {ex.Message}");
            return false;
        }
        catch (NoSuchElementException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup element missing: {ex.Message}");
            return false;
        }
        catch (ElementClickInterceptedException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup click blocked: {ex.Message}");
            return false;
        }
        catch (ElementNotInteractableException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup element not interactable: {ex.Message}");
            return false;
        }
        catch (StaleElementReferenceException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup DOM changed while interacting: {ex.Message}");
            return false;
        }
    }

    private static FastBankBindResult TryFastBindThroughMemberStore(
        IWebDriver driver,
        AccountModel account,
        string bankName,
        string bankAccount,
        string branch,
        string withdrawPassword,
        AutomationExecutionContext executionContext)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return FastBankBindResult.Unavailable;
        }

        try
        {
            var result = javaScript.ExecuteAsyncScript(
                """
                const targetBankName = arguments[0] || '';
                const accountNumber = arguments[1] || '';
                const branch = arguments[2] || '';
                const paymentPassword = arguments[3] || '';
                const suppliedPayee = arguments[4] || '';
                const done = arguments[arguments.length - 1];
                const operationState = {
                    stage: 'preflight',
                    updatedAt: Date.now()
                };
                window.__vibecodeRegBankBind = operationState;

                const compact = value => String(value || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .toLowerCase()
                    .replace(/[^a-z0-9]/g, '');

                const isRequired = value =>
                    value === true
                    || ['Y', '1', 'TRUE'].includes(String(value || '').toUpperCase());

                const wait = milliseconds =>
                    new Promise(resolve => setTimeout(resolve, milliseconds));

                const findStore = async () => {
                    for (let attempt = 0; attempt < 15; attempt++) {
                        const root = window.tcgmodulemc
                            && window.tcgmodulemc.default
                            && window.tcgmodulemc.default.memberCenterStore;
                        if (root && root.seculist) return root;
                        await wait(100);
                    }
                    return null;
                };

                const finish = (stage, result) => {
                    operationState.stage = stage;
                    operationState.result = result;
                    operationState.updatedAt = Date.now();
                    done(result);
                };
                const fallback = reason =>
                    finish('fallback', `FALLBACK:${reason}`);

                (async () => {
                    const root = await findStore();
                    const seculist = root && root.seculist;
                    if (!seculist
                        || typeof seculist.bindCard !== 'function'
                        || typeof seculist.getBankLis !== 'function') {
                        fallback('store-unavailable');
                        return;
                    }

                    const memberInfo = root.mcCommon && root.mcCommon.memberInfo
                        ? root.mcCommon.memberInfo
                        : {};
                    const requests = [];

                    if (!Array.isArray(seculist.bankList) || seculist.bankList.length === 0) {
                        requests.push(seculist.getBankLis());
                    }

                    if ((!memberInfo || typeof memberInfo !== 'object'
                        || (!memberInfo.payee && !memberInfo.mobile))
                        && root.mcCommon
                        && typeof root.mcCommon.getMemberInfo === 'function') {
                        requests.push(root.mcCommon.getMemberInfo());
                    }

                    if (requests.length > 0) {
                        await Promise.all(requests);
                    }

                    const refreshedMemberInfo = root.mcCommon && root.mcCommon.memberInfo
                        ? root.mcCommon.memberInfo
                        : memberInfo;
                    const bankTarget = compact(targetBankName);
                    const bank = (seculist.bankList || []).find(item => {
                        const candidate = compact(
                            `${item.bankLocaleName || ''} ${item.bankEngName || ''} ` +
                            `${item.bankChName || ''} ${item.bankCode || ''}`);
                        return bankTarget
                            && (candidate.includes(bankTarget) || bankTarget.includes(candidate));
                    });

                    if (!bank) {
                        fallback('bank-not-found');
                        return;
                    }

                    // The visual form is still required for banks that demand
                    // extra user input (OTP, province/city, custom fields, etc.).
                    if (isRequired(bank.isProvinceRequired)
                        || isRequired(bank.isCityRequired)
                        || isRequired(bank.isPlayerIdRequired)
                        || isRequired(bank.isPlayerEmailRequired)
                        || isRequired(bank.isPlayerPhoneRequired)
                        || isRequired(bank.isCustomizeRequired)
                        || (isRequired(bank.isBranchRequired) && !branch)) {
                        fallback('bank-requires-visual-fields');
                        return;
                    }

                    if (refreshedMemberInfo && refreshedMemberInfo.bindGA) {
                        fallback('ga-confirmation-required');
                        return;
                    }

                    let bankTemplate = null;
                    if (typeof seculist.getBankTemplate === 'function') {
                        try {
                            bankTemplate = await seculist.getBankTemplate(bank.bankCode);
                        } catch {
                            fallback('bank-template-unavailable');
                            return;
                        }
                    }

                    let templatePlayerId = '';
                    let templateLegalName = false;
                    const templateSettings = bankTemplate
                        && Array.isArray(bankTemplate.bankCardTemplateSettings)
                        ? bankTemplate.bankCardTemplateSettings
                        : [];
                    for (const setting of templateSettings) {
                        const cardItem = String(setting && setting.cardItem || '');
                        if (cardItem === 'BANK_ACCOUNT_NUMBER') {
                            templatePlayerId = accountNumber;
                            continue;
                        }

                        if (cardItem === 'BANK_BRANCH') {
                            continue;
                        }

                        if (cardItem === 'LEGAL_NAME') {
                            if (setting && (setting.isLegalNameSeparate === true
                                || String(setting.isLegalNameSeparate || '').toUpperCase() === 'Y')) {
                                fallback('bank-template-needs-separate-legal-name');
                                return;
                            }

                            templateLegalName = true;
                            continue;
                        }

                        if (cardItem) {
                            fallback('bank-template-needs-visual-fields');
                            return;
                        }
                    }

                    const language = window.localStorage.getItem('language') || 'CN';
                    const displayBankName = bank.bankLocaleName
                        || (language === 'CN' || language === 'TW'
                            ? bank.bankChName
                            : bank.bankEngName)
                        || bank.bankEngName
                        || bank.bankChName
                        || targetBankName;

                    const payload = {
                        area: branch || '',
                        paymentPassword,
                        validatePaymentPassword: paymentPassword,
                        verificationCode: '',
                        mobileNum: refreshedMemberInfo && refreshedMemberInfo.mobile
                            ? refreshedMemberInfo.mobile
                            : '',
                        cardType: 'B',
                        countryDialingCode: refreshedMemberInfo
                            && refreshedMemberInfo.countryDialingCode
                            ? refreshedMemberInfo.countryDialingCode
                            : '',
                        playerId: templatePlayerId,
                        customField: '',
                        email: '',
                        mobileNo: '',
                        bankName: displayBankName,
                        bankCode: bank.bankCode || '',
                        payeeName: refreshedMemberInfo && refreshedMemberInfo.payee
                            ? refreshedMemberInfo.payee
                            : suppliedPayee,
                        cardNumber: accountNumber,
                        ...(templateLegalName ? { fullName: suppliedPayee } : {})
                    };

                    operationState.stage = 'submitting';
                    operationState.updatedAt = Date.now();
                    try {
                        const response = await seculist.bindCard(payload);
                        const status = response && Number(response.status);
                        const responseData = response && response.data
                            ? response.data
                            : response;
                        const explicitSuccess = Boolean(
                            (response && response.success === true)
                            || (responseData && responseData.success === true));

                        if ((status >= 200 && status < 300) || explicitSuccess) {
                            finish('success', `SUCCESS:${status || 200}`);
                            return;
                        }

                        if (status > 0
                            || (responseData && responseData.success === false)) {
                            finish('rejected', `REJECTED:${status || 0}`);
                            return;
                        }

                        finish('unknown', 'UNKNOWN:unclassified-response');
                    } catch (error) {
                        const status = error && error.response
                            ? Number(error.response.status)
                            : 0;
                        if (status > 0) {
                            finish('rejected', `REJECTED:${status}`);
                            return;
                        }

                        finish('unknown', 'UNKNOWN:no-http-status');
                    }
                })().catch(() => {
                    if (operationState.stage === 'submitting') {
                        finish('unknown', 'UNKNOWN:store-error-after-submit');
                    } else {
                        fallback('store-error');
                    }
                });
                """,
                bankName,
                bankAccount,
                branch,
                withdrawPassword,
                account.FullName);

            var resultText = result?.ToString() ?? string.Empty;
            if (resultText.StartsWith("SUCCESS:", StringComparison.OrdinalIgnoreCase))
            {
                return FastBankBindResult.Success;
            }

            if (resultText.StartsWith("REJECTED:", StringComparison.OrdinalIgnoreCase))
            {
                return FastBankBindResult.Rejected;
            }

            if (resultText.StartsWith("UNKNOWN:", StringComparison.OrdinalIgnoreCase))
            {
                return VerifyBankCardLinked(driver, bankAccount, bankName)
                    ? FastBankBindResult.Success
                    : FastBankBindResult.Indeterminate;
            }

            return FastBankBindResult.Unavailable;
        }
        catch (WebDriverException)
        {
            return ResolveInterruptedFastBind(
                driver,
                bankAccount,
                bankName,
                executionContext);
        }
    }

    private static FastBankBindResult ResolveInterruptedFastBind(
        IWebDriver driver,
        string bankAccount,
        string bankName,
        AutomationExecutionContext executionContext)
    {
        var stage = ReadFastBindStage(driver);
        if (stage == "success")
        {
            return FastBankBindResult.Success;
        }

        if (stage == "rejected")
        {
            return FastBankBindResult.Rejected;
        }

        if (stage is "" or "preflight" or "fallback")
        {
            return FastBankBindResult.Unavailable;
        }

        var waitUntil = DateTime.UtcNow.AddSeconds(15);
        while (stage == "submitting" && DateTime.UtcNow < waitUntil)
        {
            executionContext.Delay(TimeSpan.FromMilliseconds(250));
            stage = ReadFastBindStage(driver);

            if (stage == "success")
            {
                return FastBankBindResult.Success;
            }

            if (stage == "rejected")
            {
                return FastBankBindResult.Rejected;
            }
        }

        if (VerifyBankCardLinked(driver, bankAccount, bankName))
        {
            return FastBankBindResult.Success;
        }

        return stage is "submitting" or "unknown"
            ? FastBankBindResult.Indeterminate
            : FastBankBindResult.Unavailable;
    }

    private static string ReadFastBindStage(IWebDriver driver)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return string.Empty;
        }

        try
        {
            return javaScript.ExecuteScript(
                    "return window.__vibecodeRegBankBind && window.__vibecodeRegBankBind.stage || '';")
                ?.ToString()
                ?.Trim()
                .ToLowerInvariant()
                ?? string.Empty;
        }
        catch (WebDriverException)
        {
            return string.Empty;
        }
    }

    private static bool VerifyBankCardLinked(
        IWebDriver driver,
        string bankAccount,
        string bankName)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return false;
        }

        try
        {
            var result = javaScript.ExecuteAsyncScript(
                """
                const accountNumber = String(arguments[0] || '');
                const targetBankName = String(arguments[1] || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .toLowerCase()
                    .replace(/[^a-z0-9]/g, '');
                const done = arguments[arguments.length - 1];
                const root = window.tcgmodulemc
                    && window.tcgmodulemc.default
                    && window.tcgmodulemc.default.memberCenterStore;
                const withdraw = root && root.withdraw;
                if (!withdraw || typeof withdraw.getBankCard !== 'function') {
                    done(false);
                    return;
                }

                Promise.resolve(withdraw.getBankCard())
                    .then(response => {
                        const value = response && response.value !== undefined
                            ? response.value
                            : response && response.data && response.data.value !== undefined
                                ? response.data.value
                                : withdraw.bankCard;
                        const collections = [
                            value && value.bankCards,
                            value && value.fullBankCardList,
                            value && value.fullSortedCardList
                        ].filter(Array.isArray);
                        const cards = collections.flat();
                        const expectedSuffix = accountNumber.length >= 4
                            ? accountNumber.slice(-4)
                            : accountNumber;
                        const visibleCardMatch = cards.some(card => {
                            const cardNumber = String(card && card.cardNumber || '');
                            const digits = cardNumber.replace(/\D/g, '');
                            const cardBankName = String(
                                card && (card.bankName || card.bankEngName || card.bankCode) || '')
                                .normalize('NFD')
                                .replace(/[\u0300-\u036f]/g, '')
                                .toLowerCase()
                                .replace(/[^a-z0-9]/g, '');
                            const bankMatches = !targetBankName
                                || !cardBankName
                                || cardBankName.includes(targetBankName)
                                || targetBankName.includes(cardBankName);
                            return bankMatches
                                && digits.length >= expectedSuffix.length
                                && digits.endsWith(expectedSuffix);
                        });
                        done(Boolean(
                            accountNumber
                            && (JSON.stringify(value || '').includes(accountNumber)
                                || visibleCardMatch)));
                    })
                    .catch(() => done(false));
                """,
                bankAccount,
                bankName);

            return result is bool linked && linked;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static void CloseOptionalPopups(IWebDriver driver, AutomationExecutionContext executionContext)
    {
        // Skip popup closing completely to optimize speed
        /*
        executionContext.Delay(TimeSpan.FromSeconds(3));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            executionContext.Checkpoint();

            if (!ClickAnyVisible(driver, PopupCloseSelectors, TimeSpan.FromSeconds(2), required: false))
            {
                // Wait another short period just in case another popup is queued
                executionContext.Delay(TimeSpan.FromSeconds(1));
                if (attempt == 0 && !ClickAnyVisible(driver, PopupCloseSelectors, TimeSpan.FromSeconds(2), required: false))
                {
                    return;
                }
            }

            executionContext.Delay(TimeSpan.FromMilliseconds(500));
        }
        */
    }

    private static void FastFillAndSubmitBankDetails(
        IWebDriver driver,
        string bankAccount,
        string branch,
        string withdrawPassword,
        AutomationExecutionContext executionContext)
    {
        try
        {
            if (driver is IJavaScriptExecutor js)
            {
                js.ExecuteScript($@"
                    function setVal(el, val) {{
                        if(!el) return;
                        var setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                        if (setter) setter.call(el, val);
                        else el.value = val;
                        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}
                    
                    var inputs = document.querySelectorAll('.type-group-area .mc-account-list input.input-base, .account-form-group input, input.input-base, input[type=password], input.form-control');
                    var accFilled = false;
                    var branchFilled = false;
                    for(var i=0; i<inputs.length; i++) {{
                        var input = inputs[i];
                        var p = (input.placeholder || '').toLowerCase();
                        if(input.type === 'password' || p.includes('mật khẩu') || p.includes('password') || p.includes('mã pin')) {{
                            setVal(input, '{withdrawPassword}');
                        }}
                        else if(!accFilled && (p.includes('tài khoản') || p.includes('account') || p.includes('số thẻ') || p.includes('stk'))) {{
                            setVal(input, '{bankAccount}');
                            accFilled = true;
                        }}
                        else if(!branchFilled && (p.includes('chi nhánh') || p.includes('branch'))) {{
                            setVal(input, '{branch}');
                            branchFilled = true;
                        }}
                    }}
                    
                    // Fallback theo vị trí nếu không nhận diện được placeholder
                    if (!accFilled) {{
                        var textInputs = document.querySelectorAll('.type-group-area .mc-account-list input[type=text], .account-form-group input[type=text]');
                        if(textInputs.length >= 2) {{
                            setVal(textInputs[0], '{bankAccount}');
                            setVal(textInputs[1], '{branch}');
                        }}
                    }}
                    
                    // Delay nhẹ để Vue kịp nhận value trước khi Submit
                    setTimeout(() => {{
                        var btns = document.querySelectorAll('.am-button, button.submit, button.confirm-btn, .btn-submit, .btn-confirm, .van-button--info, .van-button, .btn, span.am-button');
                        for(var j=0; j<btns.length; j++) {{
                            var text = (btns[j].innerText || '').toLowerCase();
                            if(text.includes('lưu') || text.includes('xác nhận') || text.includes('confirm') || text.includes('submit')) {{
                                btns[j].click();
                                break;
                            }}
                        }}
                    }}, 300);
                ");
            }
            
            // Đợi script chạy và submit
            executionContext.Delay(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    private static void NavigateToBankSetup(
        IWebDriver driver,
        AutomationExecutionContext executionContext,
        string? directBankUrl)
    {
        try
        {
            // Đập popup nhanh TRƯỚC KHI navigate
            if (string.IsNullOrWhiteSpace(directBankUrl)
                && driver is IJavaScriptExecutor js)
            {
                js.ExecuteScript(@"
                    document.querySelectorAll('.van-overlay, .modal-backdrop, .overlay, .van-dialog, .toast_bg, .registerModal, .dialog-tips').forEach(e => e.remove());
                    document.querySelectorAll('button, div, span, a, i, img').forEach(el => {
                        var text = (el.innerText || '').trim();
                        var src = (el.src || '').toLowerCase();
                        var className = (el.className || '').toLowerCase();
                        if (text === 'Huỷ' || text === 'Cancel' || text === 'Đóng' || text === 'Đã Hiểu' || text === 'Tôi Biết Rồi' || text === 'X' || 
                            src.includes('close') || className.includes('close-icon') || className.includes('close_btn')) {
                            el.click();
                        }
                    });
                ");
            }

            var addBankUrl = directBankUrl;
            if (string.IsNullOrWhiteSpace(addBankUrl))
            {
                var currentUrl = driver.Url;
                var uri = new Uri(currentUrl);

                // Xây dựng link nhảy thẳng vào form Thêm Ngân Hàng (Add Bank)
                var query = string.IsNullOrEmpty(uri.Query) ? "?type=B" : uri.Query + "&type=B";
                addBankUrl = $"{uri.Scheme}://{uri.Host}/m/securityCenter/addBankCard{query}";
            }

            // The member area is a React single-page app. A full document
            // reload repeats dozens of unrelated startup API calls and can add
            // 15-20 seconds. Prefer an authenticated same-origin SPA route and
            // retain a hard-navigation fallback for shell/version changes.
            var spaNavigationStarted = TryNavigateWithinSpa(driver, addBankUrl);
            if (spaNavigationStarted
                && WaitForBankDropdown(driver, TimeSpan.FromSeconds(22)))
            {
                return;
            }

            NavigateWithFullReload(driver, addBankUrl);
            if (!WaitForBankDropdown(driver, TimeSpan.FromSeconds(30)))
            {
                throw new WebDriverTimeoutException(
                    "Bank setup route did not expose the bank selector after SPA and full-navigation attempts.");
            }
        }
        catch (WebDriverTimeoutException)
        {
            throw;
        }
        catch
        {
            // Preserve the existing behavior for unexpected navigation
            // failures. The required dropdown wait in Execute will report a
            // precise interaction timeout.
        }
    }

    private enum FastBankBindResult
    {
        Unavailable,
        Success,
        Rejected,
        Indeterminate
    }

    private static bool TryNavigateWithinSpa(IWebDriver driver, string addBankUrl)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return false;
        }

        try
        {
            var result = javaScript.ExecuteScript(
                """
                try {
                    const target = new URL(arguments[0], window.location.href);
                    const authenticated =
                        Boolean(window.sessionStorage.getItem('MC_SESSION_INFO'))
                        || Boolean(window.sessionStorage.getItem('sessionInfo'));

                    if (target.origin !== window.location.origin
                        || !window.location.pathname.startsWith('/m/')
                        || !authenticated
                        || typeof window.PopStateEvent !== 'function') {
                        return false;
                    }

                    try { window.stop(); } catch {}
                    window.history.pushState(
                        {},
                        '',
                        target.pathname + target.search + target.hash);
                    window.dispatchEvent(
                        new PopStateEvent('popstate', { state: window.history.state }));
                    return true;
                } catch {
                    return false;
                }
                """,
                addBankUrl);

            return result is bool started && started;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static void NavigateWithFullReload(IWebDriver driver, string addBankUrl)
    {
        if (driver is IJavaScriptExecutor javaScript)
        {
            javaScript.ExecuteAsyncScript(
                """
                const url = arguments[0];
                const done = arguments[arguments.length - 1];
                setTimeout(() => window.location.replace(url), 0);
                done(true);
                """,
                addBankUrl);
            return;
        }

        driver.Navigate().GoToUrl(addBankUrl);
    }

    private static bool WaitForBankDropdown(IWebDriver driver, TimeSpan timeout)
    {
        try
        {
            var wait = CreateWait(driver, timeout);
            return wait.Until(webDriver =>
                FindVisibleElements(webDriver, BankDropdownSelectors).Any());
        }
        catch (WebDriverTimeoutException)
        {
            return false;
        }
    }

    private static void FillBankAccountAndBranch(
        IWebDriver driver,
        string bankAccount,
        string branch,
        AutomationExecutionContext executionContext)
    {
        var groupedInputs = WaitForAnyVisibleList(
            driver,
            [By.CssSelector(".type-group-area .mc-account-list input.input-base")],
            TimeSpan.FromSeconds(5));

        if (groupedInputs.Count >= 3)
        {
            executionContext.Checkpoint();
            groupedInputs[1].SendKeysLikeHuman(driver, bankAccount);

            if (!string.IsNullOrWhiteSpace(branch))
            {
                executionContext.Checkpoint();
                groupedInputs[2].SendKeysLikeHuman(driver, branch);
            }

            return;
        }

        var accountInput = WaitForAnyVisible(driver, AccountNumberSelectors, TimeSpan.FromSeconds(10));
        executionContext.Checkpoint();
        accountInput.SendKeysLikeHuman(driver, bankAccount);

        if (!string.IsNullOrWhiteSpace(branch))
        {
            var branchInput = WaitForAnyVisible(driver, BranchSelectors, TimeSpan.FromSeconds(10));
            executionContext.Checkpoint();
            branchInput.SendKeysLikeHuman(driver, branch);
        }
    }

    private static void FillWithdrawPassword(
        IWebDriver driver,
        string withdrawPassword,
        AutomationExecutionContext executionContext)
    {
        var passwordInputs = WaitForAnyVisibleList(driver, PasswordSelectors, TimeSpan.FromSeconds(10));
        if (passwordInputs.Count < 2)
        {
            throw new WebDriverTimeoutException("Could not find two withdraw password inputs.");
        }

        var firstPasswordInput = passwordInputs[^2];
        var secondPasswordInput = passwordInputs[^1];

        executionContext.Checkpoint();
        firstPasswordInput.SendKeysLikeHuman(driver, withdrawPassword);

        executionContext.Checkpoint();
        secondPasswordInput.SendKeysLikeHuman(driver, withdrawPassword);
    }

    private static void ClickBankName(IWebDriver driver, string bankName, TimeSpan timeout)
    {
        if (TryClickBankNameWithJavaScript(driver, bankName))
        {
            return;
        }

        var normalizedBankName = CompactForMatch(bankName);
        var bankItem = WaitForMatchingText(
            driver,
            BankListItemSelectors,
            text =>
            {
                var normalizedText = CompactForMatch(text);
                return !string.IsNullOrWhiteSpace(normalizedText)
                    && (normalizedText.Contains(normalizedBankName, StringComparison.OrdinalIgnoreCase)
                        || normalizedBankName.Contains(normalizedText, StringComparison.OrdinalIgnoreCase));
            },
            timeout);

        bankItem.ClickLikeHuman(driver);
    }

    private static bool TryClickBankNameWithJavaScript(
        IWebDriver driver,
        string bankName)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return false;
        }

        try
        {
            var result = javaScript.ExecuteScript(
                """
                const compact = (value) => (value || '')
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .toLowerCase()
                    .replace(/[^a-z0-9]/g, '');
                const target = compact(arguments[0]);
                if (!target) return false;

                for (const element of document.querySelectorAll('.popUps-list-item')) {
                    if (!element.offsetWidth || !element.offsetHeight) continue;
                    const text = compact(element.innerText || element.textContent);
                    if (text && (text.includes(target) || target.includes(text))) {
                        element.click();
                        return true;
                    }
                }

                return false;
                """,
                bankName);

            return result is bool clicked && clicked;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static void ClickSubmitButton(IWebDriver driver, TimeSpan timeout)
    {
        var submitButton = WaitForMatchingText(
            driver,
            SubmitSelectors,
            text =>
            {
                var normalizedText = NormalizeForMatch(text);
                return ContainsAny(normalizedText, "xac nhan", "luu", "confirm", "save");
            },
            timeout);

        submitButton.ClickLikeHuman(driver);
    }

    private static void ClickConfirmationPopupIfAny(IWebDriver driver, AutomationExecutionContext executionContext)
    {
        // Không chờ cứng 2s nữa — dùng WebDriverWait ngay (tối đa 3s, thoát ngay khi không có popup)
        try
        {
            var wait = CreateWait(driver, TimeSpan.FromSeconds(3));
            var confirmButton = wait.Until(webDriver =>
            {
                var elements = FindVisibleElements(webDriver, [
                    By.CssSelector(".am-modal-button"),
                    By.CssSelector(".am-dialog-button"),
                    By.CssSelector(".dialog-btn"),
                    By.CssSelector(".van-dialog__confirm"),
                    By.CssSelector(".am-modal button"),
                    By.CssSelector(".am-dialog button"),
                    By.CssSelector(".van-dialog button"),
                    By.CssSelector(".dialog button")
                ]);
                
                return elements.FirstOrDefault(element =>
                {
                    var normalizedText = NormalizeForMatch(element.Text ?? string.Empty);
                    return ContainsAny(normalizedText, "xac nhan", "confirm", "dong y", "yes", "ok");
                });
            });
            
            if (confirmButton != null)
            {
                if (driver is IJavaScriptExecutor js)
                {
                    js.ExecuteScript("arguments[0].click();", confirmButton);
                }
                else
                {
                    confirmButton.ClickLikeHuman(driver);
                }
                
                executionContext.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
        catch (WebDriverTimeoutException)
        {
            // No confirmation popup appeared, which is completely fine.
        }
    }

    private static bool ClickBestTextMatch(
        IWebDriver driver,
        IReadOnlyList<By> selectors,
        IReadOnlyList<string> keywords,
        TimeSpan timeout)
    {
        try
        {
            var element = WaitForMatchingText(
                driver,
                selectors,
                text => ContainsAny(NormalizeForMatch(text), keywords),
                timeout);

            element.ClickLikeHuman(driver);
            return true;
        }
        catch (WebDriverTimeoutException)
        {
            return false;
        }
    }

    private static bool ClickAnyVisible(IWebDriver driver, IReadOnlyList<By> selectors, TimeSpan timeout, bool required)
    {
        try
        {
            var element = WaitForAnyVisible(driver, selectors, timeout);
            element.ClickLikeHuman(driver);
            return true;
        }
        catch (WebDriverTimeoutException) when (!required)
        {
            return false;
        }
    }

    private static IWebElement WaitForAnyVisible(IWebDriver driver, IReadOnlyList<By> selectors, TimeSpan timeout)
    {
        var wait = CreateWait(driver, timeout);
        var element = wait.Until(webDriver => FindVisibleElements(webDriver, selectors).FirstOrDefault());
        return element ?? throw new WebDriverTimeoutException("Could not find a visible matching element.");
    }

    private static IReadOnlyList<IWebElement> WaitForAnyVisibleList(
        IWebDriver driver,
        IReadOnlyList<By> selectors,
        TimeSpan timeout)
    {
        try
        {
            var wait = CreateWait(driver, timeout);
            return wait.Until(webDriver =>
            {
                var elements = FindVisibleElements(webDriver, selectors).ToArray();
                return elements.Length > 0 ? elements : null;
            }) ?? [];
        }
        catch (WebDriverTimeoutException)
        {
            return [];
        }
    }

    private static IWebElement WaitForMatchingText(
        IWebDriver driver,
        IReadOnlyList<By> selectors,
        Func<string, bool> isMatch,
        TimeSpan timeout)
    {
        var wait = CreateWait(driver, timeout);
        var element = wait.Until(webDriver =>
        {
            return FindVisibleElements(webDriver, selectors)
                .FirstOrDefault(element => isMatch(element.Text ?? string.Empty));
        });

        return element ?? throw new WebDriverTimeoutException("Could not find a visible element matching the expected text.");
    }

    private static IEnumerable<IWebElement> FindVisibleElements(IWebDriver driver, IReadOnlyList<By> selectors)
    {
        foreach (var selector in selectors)
        {
            IReadOnlyCollection<IWebElement> elements;

            try
            {
                elements = driver.FindElements(selector);
            }
            catch (NoSuchElementException)
            {
                continue;
            }
            catch (StaleElementReferenceException)
            {
                continue;
            }
            catch (ElementNotInteractableException)
            {
                continue;
            }

            foreach (var element in elements)
            {
                if (IsDisplayedAndEnabled(element))
                {
                    yield return element;
                }
            }
        }
    }

    private static bool IsDisplayedAndEnabled(IWebElement element)
    {
        try
        {
            return element.Displayed && element.Enabled;
        }
        catch (StaleElementReferenceException)
        {
            return false;
        }
        catch (ElementNotInteractableException)
        {
            return false;
        }
    }

    private static WebDriverWait CreateWait(IWebDriver driver, TimeSpan timeout)
    {
        var wait = new WebDriverWait(driver, timeout)
        {
            PollingInterval = TimeSpan.FromMilliseconds(250)
        };

        wait.IgnoreExceptionTypes(
            typeof(NoSuchElementException),
            typeof(StaleElementReferenceException),
            typeof(ElementNotInteractableException));

        return wait;
    }

    private static bool ContainsAny(string haystack, IReadOnlyList<string> needles)
    {
        return needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
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

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
    }

    private static string CompactForMatch(string? value)
    {
        return string.Concat(
            NormalizeForMatch(value)
                .Where(char.IsLetterOrDigit));
    }
}
