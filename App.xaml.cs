using System;
using System.IO;
using System.Windows;
using BAOCODETOOL.Views;

namespace BAOCODETOOL;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private BAOCODETOOL.TelegramBot.BotService? _botService;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

            if (mainWindow.DataContext is BAOCODETOOL.ViewModels.MainViewModel mainViewModel)
            {
                // Replace this with your actual Bot Token
                _botService = new BAOCODETOOL.TelegramBot.BotService("8965755551:AAHHDjVvwm_BFwrnacT0-9DDj1i3j1dg6P8", mainViewModel);
                _botService.Start();
            }
        }
        catch (Exception ex)
        {
            TryWriteStartupLog(ex);
            MessageBox.Show(
                "The app could not start. A startup log was written next to the executable.",
                "BAOCODETOOL",
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
