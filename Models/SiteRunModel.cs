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
}
