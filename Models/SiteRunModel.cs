using CommunityToolkit.Mvvm.ComponentModel;

namespace BAOCODETOOL.Models;

public partial class SiteRunModel : ObservableObject
{
    [ObservableProperty]
    private TargetSite _site;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private AccountStatus _status = AccountStatus.Ready;

    [ObservableProperty]
    private string _logMessage = "Ready.";

    [ObservableProperty]
    private bool _registrationCompleted;

    [ObservableProperty]
    private bool _requiresManualBankChange;
}
