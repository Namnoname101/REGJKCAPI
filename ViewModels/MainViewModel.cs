using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenQA.Selenium;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VibecodeReg.Core;
using VibecodeReg.Models;
using VibecodeReg.Views;

namespace VibecodeReg.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const string TestProxyEnvironmentVariable = "VIBECODE_TEST_PROXY";
    private const string DashboardView = "Dashboard";
    private const string ProfilesView = "Profiles";
    private const string SettingsView = "Settings";
    private const string LogsView = "Logs";

    private readonly BotManager _botManager = new();
    private readonly ConcurrentBag<IWebDriver> _manualDrivers = new();

    public MainViewModel()
    {
        var loadedProfiles = ProfileStore.LoadProfiles();
        foreach (var profile in loadedProfiles)
        {
            Profiles.Add(profile);
        }

        Profiles.CollectionChanged += ProfilesOnCollectionChanged;
        SeedTestProfileIfRequested();
    }

    public ObservableCollection<AccountModel> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _maxThreads = 3;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private bool _isPaused;

    [ObservableProperty]
    private string _runStateText = "Ready";

    [ObservableProperty]
    private string _selectedNavigation = ProfilesView;

    public bool IsDashboardSelected
    {
        get => SelectedNavigation == DashboardView;
        set
        {
            if (value)
            {
                SelectedNavigation = DashboardView;
            }
        }
    }

    public bool IsProfilesSelected
    {
        get => SelectedNavigation == ProfilesView;
        set
        {
            if (value)
            {
                SelectedNavigation = ProfilesView;
            }
        }
    }

    public bool IsSettingsSelected
    {
        get => SelectedNavigation == SettingsView;
        set
        {
            if (value)
            {
                SelectedNavigation = SettingsView;
            }
        }
    }

    public bool IsLogsSelected
    {
        get => SelectedNavigation == LogsView;
        set
        {
            if (value)
            {
                SelectedNavigation = LogsView;
            }
        }
    }

    public int TotalProfiles => Profiles.Count;

    public int RunningProfiles => Profiles.Count(account => account.Status == AccountStatus.Running);

    public int SuccessfulProfiles => Profiles.Count(account => account.Status == AccountStatus.Success);

    public int FailedProfiles => Profiles.Count(account => account.Status == AccountStatus.Failed);

    public int StoppedProfiles => Profiles.Count(account => account.Status == AccountStatus.Stopped);

    public int TotalRunCount => Profiles
        .SelectMany(account => account.SiteRuns)
        .Count(siteRun => siteRun.IsSelected);

    public int CompletedRunCount => Profiles
        .SelectMany(account => account.SiteRuns)
        .Count(siteRun => siteRun.IsSelected
            && siteRun.Status is AccountStatus.Success or AccountStatus.Failed or AccountStatus.Stopped);

    public double ProgressValue => TotalRunCount == 0
        ? 0
        : Math.Round((double)CompletedRunCount / TotalRunCount * 100, 1);

    public string ProgressText => $"{CompletedRunCount}/{TotalRunCount} runs";

    partial void OnMaxThreadsChanged(int value)
    {
        if (value < 1)
        {
            MaxThreads = 1;
        }
    }

    partial void OnSelectedNavigationChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsProfilesSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsLogsSelected));
    }

    [RelayCommand]
    private void AddProfile()
    {
        var dialogViewModel = new ProfileDialogViewModel();
        var dialog = new ProfileDialog
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current?.MainWindow,
            DataContext = dialogViewModel
        };

        if (dialog.ShowDialog() == true)
        {
            var account = dialogViewModel.ToAccount();
            Profiles.Add(account);
            ProfileStore.SaveProfiles(Profiles);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSelected))]
    private async Task StartSelectedAsync(AccountModel? account)
    {
        if (account is null)
        {
            return;
        }

        await RunProfilesAsync(new[] { account }, 1);
    }

    [RelayCommand]
    private void OpenBrowser(AccountModel? selectedAccount)
    {
        if (selectedAccount is null)
        {
            return;
        }

        AutomationUi.SetStatus(selectedAccount, logMessage: "Opening manual browser profile...");

        _ = Task.Run(() =>
        {
            try
            {
                var driver = DriverFactory.CreateDriver(selectedAccount);
                _manualDrivers.Add(driver);

                AutomationUi.SetStatus(selectedAccount, logMessage: "Manual browser profile opened.");
            }
            catch (Exception ex)
            {
                AutomationUi.SetStatus(selectedAccount, logMessage: $"Failed to open manual browser profile: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private void CopyUsername(AccountModel? selectedAccount)
    {
        if (selectedAccount != null && !string.IsNullOrEmpty(selectedAccount.Username))
        {
            Clipboard.SetText(selectedAccount.Username);
        }
    }

    [RelayCommand]
    private void CopyPassword(AccountModel? selectedAccount)
    {
        if (selectedAccount != null && !string.IsNullOrEmpty(selectedAccount.Password))
        {
            Clipboard.SetText(selectedAccount.Password);
        }
    }

    [RelayCommand]
    private void EditProfile(AccountModel? selectedAccount)
    {
        if (selectedAccount is null)
        {
            return;
        }

        if (selectedAccount.Status == AccountStatus.Running)
        {
            MessageBox.Show("Cannot edit a profile while it is running. Please stop the task first.", 
                "Edit Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialogViewModel = new ProfileDialogViewModel(selectedAccount);
        var dialog = new ProfileDialog
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current?.MainWindow,
            DataContext = dialogViewModel
        };

        if (dialog.ShowDialog() == true)
        {
            dialogViewModel.UpdateAccount(selectedAccount);
            ProfileStore.SaveProfiles(Profiles);
        }
    }

    [RelayCommand]
    private void DuplicateProfile(AccountModel? selectedAccount)
    {
        if (selectedAccount is null)
        {
            return;
        }

        var dialogViewModel = new ProfileDialogViewModel(selectedAccount);
        dialogViewModel.Username += "_copy";

        var dialog = new ProfileDialog
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current?.MainWindow,
            DataContext = dialogViewModel
        };

        if (dialog.ShowDialog() == true)
        {
            var account = dialogViewModel.ToAccount();
            Profiles.Add(account);
            ProfileStore.SaveProfiles(Profiles);
        }
    }

    [RelayCommand]
    private void ClearProfileData(AccountModel? selectedAccount)
    {
        if (selectedAccount is null)
        {
            return;
        }

        if (selectedAccount.Status == AccountStatus.Running)
        {
            MessageBox.Show("Cannot clear data while the profile is running. Please stop the task first.", 
                "Clear Data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"Are you sure you want to clear browser data for profile '{selectedAccount.Username}'?\nThis will remove all cookies, history, and cache, but keep the account settings intact.", 
            "Confirm Clear Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (System.IO.Directory.Exists(selectedAccount.ProfileFolderPath))
            {
                System.IO.Directory.Delete(selectedAccount.ProfileFolderPath, true);
                AutomationUi.SetStatus(selectedAccount, logMessage: "Browser data cleared successfully.");
            }
            else
            {
                AutomationUi.SetStatus(selectedAccount, logMessage: "Browser data folder did not exist (already clean).");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to clear data folder: {ex.Message}", 
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void DeleteProfile(AccountModel? selectedAccount)
    {
        if (selectedAccount is null)
        {
            return;
        }

        if (selectedAccount.Status == AccountStatus.Running)
        {
            MessageBox.Show("Cannot delete a profile while it is running. Please stop the task first.", 
                "Delete Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"Are you sure you want to delete profile '{selectedAccount.Username}'?\nThis will permanently delete the browser data on disk.", 
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Profiles.Remove(selectedAccount);
        ProfileStore.SaveProfiles(Profiles);

        try
        {
            if (System.IO.Directory.Exists(selectedAccount.ProfileFolderPath))
            {
                System.IO.Directory.Delete(selectedAccount.ProfileFolderPath, true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Profile removed from list, but failed to delete data folder: {ex.Message}", 
                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        await RunProfilesAsync(Profiles.ToList(), MaxThreads);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _botManager.Pause();
        IsPaused = true;
        RunStateText = "Paused";
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _botManager.Resume();
        IsPaused = false;
        RunStateText = "Running";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _botManager.Stop();
        IsPaused = false;
        RunStateText = "Stopping...";
    }

    private bool CanStart() => !IsRunning && Profiles.Any(HasRunnableSites);

    private bool CanStartSelected(AccountModel? account) => !IsRunning && account is not null && HasRunnableSites(account);

    private bool CanPause() => IsRunning && !IsPaused;

    private bool CanResume() => IsRunning && IsPaused;

    private bool CanStop() => IsRunning;

    private async Task RunProfilesAsync(IReadOnlyCollection<AccountModel> profiles, int maxThreads)
    {
        if (IsRunning || profiles.Count == 0)
        {
            return;
        }

        IsRunning = true;
        IsPaused = false;
        RunStateText = "Running";
        NotifyExecutionMetricsChanged();

        try
        {
            await _botManager.RunMassRegistrationAsync(profiles, maxThreads, () =>
            {
                Application.Current.Dispatcher.Invoke(() => ProfileStore.SaveProfiles(Profiles));
            });
        }
        catch (InvalidOperationException ex)
        {
            RunStateText = ex.Message;
        }
        finally
        {
            IsPaused = false;
            IsRunning = false;
            RunStateText = GetPostRunStateText();
            NotifyExecutionMetricsChanged();
        }
    }

    private static bool HasRunnableSites(AccountModel account)
    {
        return account.SiteRuns.Any(siteRun => siteRun.IsSelected
            && siteRun.Status != AccountStatus.Running
            && siteRun.Status != AccountStatus.Success);
    }

    private string GetPostRunStateText()
    {
        var selectedRuns = Profiles.SelectMany(account => account.SiteRuns).Where(siteRun => siteRun.IsSelected).ToList();

        if (selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Stopped))
        {
            return "Stopped";
        }

        if (selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Failed))
        {
            return "Completed with failures";
        }

        if (selectedRuns.Count > 0 && selectedRuns.All(siteRun => siteRun.Status == AccountStatus.Success))
        {
            return "Completed";
        }

        return "Ready";
    }

    private void ProfilesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AccountModel account in e.OldItems)
            {
                account.PropertyChanged -= AccountOnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (AccountModel account in e.NewItems)
            {
                account.PropertyChanged += AccountOnPropertyChanged;
            }
        }

        NotifyExecutionMetricsChanged();
        StartCommand.NotifyCanExecuteChanged();
        StartSelectedCommand.NotifyCanExecuteChanged();
    }

    private void SeedTestProfileIfRequested()
    {
        if (Profiles.Count > 0)
        {
            return;
        }

        var rawProxy = Environment.GetEnvironmentVariable(TestProxyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawProxy))
        {
            return;
        }

        var account = new AccountModel
        {
            Username = "test.user01",
            Password = "Test@12345",
            PhoneNumber = "0987654321",
            FullName = "Test User",
            Level2Password = "123456",
            AccountNumber = "1234567890",
            BankName = "Vietcombank",
            Branch = "Main Branch",
            Proxy = rawProxy.Trim(),
            UserAgent = string.Empty,
            Status = AccountStatus.Ready,
            LogMessage = "Seeded test profile."
        };

        Profiles.Add(account);
    }

    private void SiteRunOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SiteRunModel.Status))
        {
            NotifyExecutionMetricsChanged();

            if (sender is SiteRunModel siteRun && (siteRun.Status == AccountStatus.Success || siteRun.Status == AccountStatus.Failed))
            {
                ProfileStore.SaveProfiles(Profiles);
            }
        }
    }

    private void AccountOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountModel.HasSelectedSites)
            or nameof(AccountModel.Status)
            or nameof(AccountModel.SiteStatusText)
            or nameof(AccountModel.SiteLogText))
        {
            NotifyExecutionMetricsChanged();
            StartCommand.NotifyCanExecuteChanged();
            StartSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyExecutionMetricsChanged()
    {
        OnPropertyChanged(nameof(TotalProfiles));
        OnPropertyChanged(nameof(RunningProfiles));
        OnPropertyChanged(nameof(SuccessfulProfiles));
        OnPropertyChanged(nameof(FailedProfiles));
        OnPropertyChanged(nameof(StoppedProfiles));
        OnPropertyChanged(nameof(TotalRunCount));
        OnPropertyChanged(nameof(CompletedRunCount));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressText));
    }

    public void Dispose()
    {
        while (_manualDrivers.TryTake(out var driver))
        {
            try
            {
                driver.Dispose();
            }
            catch (WebDriverException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
