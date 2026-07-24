using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.ViewModels;

public partial class ProfileDialogViewModel : ObservableObject
{
    public ProfileDialogViewModel(AccountModel? existingAccount = null)
    {
        SiteRuns = new ObservableCollection<SiteRunModel>(
            Enum.GetValues<TargetSite>().Select(site => new SiteRunModel { Site = site, IsSelected = true }));

        foreach (var siteRun in SiteRuns)
        {
            siteRun.PropertyChanged += SiteRunOnPropertyChanged;
        }

        if (existingAccount != null)
        {
            Username = existingAccount.Username;
            Password = existingAccount.Password;
            PhoneNumber = existingAccount.PhoneNumber;
            FullName = existingAccount.FullName;
            Level2Password = existingAccount.Level2Password;
            AccountNumber = existingAccount.AccountNumber;
            BankName = existingAccount.BankName;
            Branch = existingAccount.Branch;
            Proxy = existingAccount.Proxy;
            UserAgent = existingAccount.UserAgent;

            foreach (var siteRun in SiteRuns)
            {
                var existingSiteRun = existingAccount.SiteRuns.FirstOrDefault(sr => sr.Site == siteRun.Site);
                if (existingSiteRun != null)
                {
                    siteRun.IsSelected = existingSiteRun.IsSelected;
                }
            }
        }
    }

    public event Action<bool?>? CloseRequested;

    public ObservableCollection<SiteRunModel> SiteRuns { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _phoneNumber = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _fullName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _level2Password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _accountNumber = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _bankName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _branch = string.Empty;

    [ObservableProperty]
    private string _proxy = string.Empty;

    [ObservableProperty]
    private string _userAgent = string.Empty;

    public AccountModel ToAccount()
    {
        var account = new AccountModel
        {
            Status = AccountStatus.Ready,
            LogMessage = "Profile ready."
        };
        UpdateAccount(account);
        
        foreach (var accountSiteRun in account.SiteRuns)
        {
            accountSiteRun.Status = AccountStatus.Ready;
            accountSiteRun.LogMessage = accountSiteRun.IsSelected ? "Ready." : "Skipped.";
        }

        return account;
    }

    public void UpdateAccount(AccountModel account)
    {
        account.Username = Username.Trim();
        account.Password = Password.Trim();
        account.PhoneNumber = PhoneNumber.Trim();
        account.FullName = FullName.Trim();
        account.Level2Password = Level2Password.Trim();
        account.AccountNumber = AccountNumber.Trim();
        account.BankName = BankName.Trim();
        account.Branch = Branch.Trim();
        account.Proxy = Proxy.Trim();
        account.UserAgent = UserAgent.Trim();

        foreach (var accountSiteRun in account.SiteRuns)
        {
            var selectedSiteRun = SiteRuns.First(siteRun => siteRun.Site == accountSiteRun.Site);
            accountSiteRun.IsSelected = selectedSiteRun.IsSelected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(false);
    }

    [RelayCommand]
    private async Task GenerateRandomUserAgent()
    {
        UserAgent = "Fetching fresh UA...";
        UserAgent = await UserAgentHelper.GetFreshRandomUserAgentAsync();
    }

    [RelayCommand]
    private void GenerateRandomPassword()
    {
        Password = GenerateRandomPasswordString();
    }

    private static string GenerateRandomPasswordString()
    {
        var random = new Random();
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";

        var password = new char[10];
        password[0] = upper[random.Next(upper.Length)];
        password[1] = lower[random.Next(lower.Length)];
        password[2] = digits[random.Next(digits.Length)];
        password[3] = digits[random.Next(digits.Length)];

        const string all = upper + lower + digits;
        for (int i = 4; i < 10; i++)
        {
            password[i] = all[random.Next(all.Length)];
        }

        return new string(password.OrderBy(x => random.Next()).ToArray());
    }

    private bool CanConfirm() =>
        !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(PhoneNumber)
        && !string.IsNullOrWhiteSpace(FullName)
        && !string.IsNullOrWhiteSpace(Level2Password)
        && !string.IsNullOrWhiteSpace(AccountNumber)
        && !string.IsNullOrWhiteSpace(BankName)
        && !string.IsNullOrWhiteSpace(Branch)
        && SiteRuns.Any(siteRun => siteRun.IsSelected);

    private void SiteRunOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SiteRunModel.IsSelected))
        {
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }
}
