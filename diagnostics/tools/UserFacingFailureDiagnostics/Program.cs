using System.Reflection;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;

const BindingFlags privateStatic = BindingFlags.NonPublic | BindingFlags.Static;

var registrationFormatter = typeof(BotManager).GetMethod(
    "BuildRegistrationFailureMessage",
    privateStatic,
    binder: null,
    types: [typeof(DirectRegistrationOutcome)],
    modifiers: null)
    ?? throw new InvalidOperationException("Registration failure formatter was not found.");

var phoneOutcome = new DirectRegistrationOutcome(
    DirectRegistrationStatus.Failed,
    "Số Điện Thoại đã được đăng ký, vui lòng thử Số Điện Thoại khác (#1350-test)");
var phoneMessage = (string?)registrationFormatter.Invoke(null, [phoneOutcome]);
Equal(
    "Số điện thoại đã được đăng ký, vui lòng thử số điện thoại khác.",
    phoneMessage,
    "phone message");
False(
    phoneMessage?.Contains("Direct HTTP", StringComparison.OrdinalIgnoreCase) == true,
    "phone message must not expose the direct HTTP implementation");

var bankClassifier = typeof(BotManager).GetMethod(
    "IsBankAccountAlreadyUsed",
    privateStatic,
    binder: null,
    types: [typeof(string), typeof(string)],
    modifiers: null)
    ?? throw new InvalidOperationException("Bank conflict classifier was not found.");
var bankConflict = (bool?)bankClassifier.Invoke(
    null,
    [
        "GG88 bank bind failed",
        "Tài khoản rút tiền đó đã được liên kết bởi một thành viên khác"
    ]);
Equal(true, bankConflict, "bank conflict");

var account = new AccountModel
{
    Username = "account123",
    Password = "password123",
    FullName = "NGUYEN VAN AN",
    PhoneNumber = "0912345678",
    BankName = "Vietcombank",
    AccountNumber = "1234567890",
    Level2Password = "112233"
};
foreach (var run in account.SiteRuns)
{
    run.IsSelected = run.Site == TargetSite.GG88;
}

var siteRun = account.SiteRuns.Single(run => run.Site == TargetSite.GG88);
siteRun.Status = AccountStatus.Failed;
siteRun.RegistrationCompleted = true;
siteRun.RequiresManualBankChange = true;
siteRun.LogMessage =
    "Tài khoản đã đăng ký nhưng ngân hàng này đã được sử dụng. TK/MK đã được xuất để bạn đăng nhập và đổi ngân hàng.";

var job = new RegistrationJob
{
    ChatId = 1,
    Account = account,
    StatusMessageId = 1
};
var progressBuilder = typeof(ResultSenderService).GetMethod(
    "BuildProgressText",
    privateStatic)
    ?? throw new InvalidOperationException("Telegram progress formatter was not found.");
var resultText = (string?)progressBuilder.Invoke(null, [job]) ?? string.Empty;
Contains(resultText, "account123", "registered username");
Contains(resultText, "password123", "registered password");
Contains(resultText, "đổi ngân hàng", "manual bank guidance");
False(
    resultText.Contains("Direct HTTP", StringComparison.OrdinalIgnoreCase),
    "Telegram result must not expose the direct HTTP implementation");

siteRun.RegistrationCompleted = false;
siteRun.RequiresManualBankChange = false;
siteRun.LogMessage = "Proxy die.";
var proxyText = (string?)progressBuilder.Invoke(null, [job]) ?? string.Empty;
Contains(proxyText, "Proxy die", "proxy failure");

Console.WriteLine(
    "USER-FACING FAILURE DIAGNOSTICS: PASS (phone, bank recovery credentials, proxy)");
return 0;

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{label}: expected '{expected}', actual '{actual}'.");
    }
}

static void Contains(string value, string expected, string label)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"{label}: expected output to contain '{expected}'.");
    }
}

static void False(bool value, string label)
{
    if (value)
    {
        throw new InvalidOperationException($"{label}: expected false.");
    }
}
