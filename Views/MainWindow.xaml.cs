using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BAOCODETOOL.ViewModels;

namespace BAOCODETOOL.Views;

public partial class MainWindow : Window
{
    private static readonly Regex NonDigitRegex = new("[^0-9]+", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }

    private void NumericOnly(object sender, TextCompositionEventArgs e)
    {
        e.Handled = NonDigitRegex.IsMatch(e.Text);
    }

    private void NumericPasteOnly(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;
        if (NonDigitRegex.IsMatch(pastedText))
        {
            e.CancelCommand();
        }
    }

    private void ProfilesGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var row = FindVisualParent<DataGridRow>(source);
        if (row is null)
        {
            return;
        }

        row.Focus();
        row.IsSelected = true;
    }

    private static T? FindVisualParent<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;

        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
