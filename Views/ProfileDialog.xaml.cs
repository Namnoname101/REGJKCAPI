using System;
using System.Windows;
using BAOCODETOOL.ViewModels;

namespace BAOCODETOOL.Views;

public partial class ProfileDialog : Window
{
    private ProfileDialogViewModel? _viewModel;

    public ProfileDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        AttachViewModel(null);
        base.OnClosed(e);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as ProfileDialogViewModel);
    }

    private void AttachViewModel(ProfileDialogViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(bool? result)
    {
        DialogResult = result;
        Close();
    }
}
