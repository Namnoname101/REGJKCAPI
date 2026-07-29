using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

/// <summary>
/// Keeps a small, interactive control surface in the Windows notification
/// area while the local web host and Telegram bot run in the background.
/// </summary>
public sealed class SystemTrayIconService : IHostedService, IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LocalDashboardLauncherOptions _dashboard;
    private readonly ILogger<SystemTrayIconService> _logger;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private ApplicationContext? _applicationContext;
    private Control? _dispatcher;
    private NotifyIcon? _notifyIcon;
    private int _exitRequested;
    private int _disposed;

    public SystemTrayIconService(
        IHostApplicationLifetime lifetime,
        LocalDashboardLauncherOptions dashboard,
        ILogger<SystemTrayIconService> logger)
    {
        _lifetime = lifetime;
        _dashboard = dashboard;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            _logger.LogInformation("System tray icon is unavailable in this session.");
            _started.TrySetResult();
            _stopped.TrySetResult();
            return;
        }

        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "VibeCodeReg notification area"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        await _started.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_thread is null)
        {
            return;
        }

        RequestExit();

        // Never delay host shutdown indefinitely if Windows has already torn
        // down the interactive desktop. The icon thread is a background
        // thread and will not keep the process alive.
        await Task.WhenAny(
            _stopped.Task,
            Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    private void RunMessageLoop()
    {
        ContextMenuStrip? menu = null;
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A hidden control gives StopAsync a thread-safe way to post the
            // exit request to this STA message loop.
            _dispatcher = new Control();
            _dispatcher.CreateControl();

            if (Volatile.Read(ref _exitRequested) != 0)
            {
                _started.TrySetResult();
                return;
            }

            menu = new ContextMenuStrip();
            menu.Items.Add("Mở dashboard", null, (_, _) => OpenDashboard());
            menu.Items.Add(new ToolStripSeparator());
            var stopItem = menu.Items.Add("Dừng ứng dụng");
            stopItem.Click += (_, _) =>
            {
                if (!stopItem.Enabled)
                {
                    return;
                }

                stopItem.Enabled = false;
                if (_notifyIcon is not null)
                {
                    _notifyIcon.Text = "VibeCodeReg đang dừng";
                }

                _lifetime.StopApplication();
            };

            _applicationContext = new ApplicationContext();
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "VibeCodeReg đang chạy",
                Visible = true,
                ContextMenuStrip = menu
            };
            _notifyIcon.DoubleClick += (_, _) => OpenDashboard();

            _started.TrySetResult();
            Application.Run(_applicationContext);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not create the Windows system tray icon.");
            _started.TrySetResult();
        }
        finally
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            menu?.Dispose();
            _dispatcher?.Dispose();
            _dispatcher = null;
            _applicationContext?.Dispose();
            _applicationContext = null;
            _stopped.TrySetResult();
        }
    }

    private void OpenDashboard()
    {
        LocalDashboardLauncherService.OpenDashboard(_dashboard.Url, _logger);
    }

    private void RequestExit()
    {
        Interlocked.Exchange(ref _exitRequested, 1);

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke((Action)(() => _applicationContext?.ExitThread()));
        }
        catch (InvalidOperationException)
        {
            // The desktop is already shutting down.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RequestExit();
    }
}
