using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.ViewModels;

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
