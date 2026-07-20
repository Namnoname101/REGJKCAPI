using System;
using System.IO;
using System.Windows;
using VibecodeReg.Views;

namespace VibecodeReg;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            TryWriteStartupLog(ex);
            MessageBox.Show(
                "The app could not start. A startup log was written next to the executable.",
                "VibecodeReg",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void TryWriteStartupLog(Exception exception)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.WriteAllText(logPath, exception.ToString());
        }
        catch
        {
        }
    }
}
