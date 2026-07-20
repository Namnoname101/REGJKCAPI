using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace VibecodeReg.Models;

public partial class AccountModel : ObservableObject
{
    public AccountModel()
    {
        SiteRuns = new ObservableCollection<SiteRunModel>(
            Enum.GetValues<TargetSite>().Select(site => new SiteRunModel { Site = site }));

        SiteRuns.CollectionChanged += SiteRunsOnCollectionChanged;

        foreach (var siteRun in SiteRuns)
        {
            siteRun.PropertyChanged += SiteRunOnPropertyChanged;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileFolderPath))]
    private Guid _profileId = Guid.NewGuid();

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _phoneNumber = string.Empty;

    [ObservableProperty]
    private string _fullName = string.Empty;

    [ObservableProperty]
    private string _level2Password = string.Empty;

    [ObservableProperty]
    private string _accountNumber = string.Empty;

    [ObservableProperty]
    private string _bankName = string.Empty;

    [ObservableProperty]
    private string _branch = string.Empty;

    [ObservableProperty]
    private string _proxy = string.Empty;

    [ObservableProperty]
    private string _userAgent = string.Empty;

    [ObservableProperty]
    private AccountStatus _status = AccountStatus.Ready;

    [ObservableProperty]
    private string _logMessage = "Profile ready.";

    public string ProfileFolderPath =>
        Path.Combine(AppContext.BaseDirectory, "Profiles", ProfileId.ToString("N"));

    public ObservableCollection<SiteRunModel> SiteRuns { get; }

    public bool HasSelectedSites => SiteRuns.Any(siteRun => siteRun.IsSelected);

    public string SelectedSitesText
    {
        get
        {
            var selectedSites = SiteRuns.Where(siteRun => siteRun.IsSelected).Select(siteRun => siteRun.Site.ToString());
            var selectedSiteText = string.Join(", ", selectedSites);
            return string.IsNullOrWhiteSpace(selectedSiteText) ? "No site selected" : selectedSiteText;
        }
    }

    public string SiteStatusText =>
        string.Join(" | ", SiteRuns.Where(siteRun => siteRun.IsSelected).Select(siteRun => $"{siteRun.Site}: {siteRun.Status}"));

    public string SiteLogText =>
        string.Join(" | ", SiteRuns.Where(siteRun => siteRun.IsSelected).Select(siteRun => $"{siteRun.Site}: {siteRun.LogMessage}"));

    public string GetProfileFolderPath(TargetSite site) =>
        Path.Combine(ProfileFolderPath, site.ToString());

    private void SiteRunsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SiteRunModel siteRun in e.OldItems)
            {
                siteRun.PropertyChanged -= SiteRunOnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SiteRunModel siteRun in e.NewItems)
            {
                siteRun.PropertyChanged += SiteRunOnPropertyChanged;
            }
        }

        NotifySiteRunSummaryChanged();
    }

    private void SiteRunOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SiteRunModel.IsSelected)
            or nameof(SiteRunModel.Status)
            or nameof(SiteRunModel.LogMessage))
        {
            NotifySiteRunSummaryChanged();
        }
    }

    private void NotifySiteRunSummaryChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSites));
        OnPropertyChanged(nameof(SelectedSitesText));
        OnPropertyChanged(nameof(SiteStatusText));
        OnPropertyChanged(nameof(SiteLogText));
    }
}
