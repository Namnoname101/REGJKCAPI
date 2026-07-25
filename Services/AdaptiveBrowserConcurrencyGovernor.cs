using System.Diagnostics;
using System.Runtime.InteropServices;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class AdaptiveBrowserConcurrencyGovernor : IBrowserConcurrencyGovernor, IDisposable
{
    private readonly AutomationOptions _options;
    private readonly ILogger<AdaptiveBrowserConcurrencyGovernor> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly CpuSampler _cpuSampler = new();
    private readonly int _maxActiveSessions;
    private readonly object _admissionSync = new();
    private long _lastAdmissionTimestamp;
    private int _consecutiveLowSamples;
    private int _activeLeases;
    private int _disposed;

    public AdaptiveBrowserConcurrencyGovernor(
        IOptions<AutomationOptions> options,
        ILogger<AdaptiveBrowserConcurrencyGovernor> logger)
    {
        _options = options.Value;
        _logger = logger;
        _maxActiveSessions = Math.Max(1, _options.MaxActiveBrowserSessions);
        _slots = new SemaphoreSlim(_maxActiveSessions, _maxActiveSessions);
    }

    public IBrowserConcurrencyLease Acquire(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AdaptiveBrowserConcurrencyGovernor));
        }

        var throttled = false;

        while (true)
        {
            _slots.Wait(cancellationToken);

            var admit = false;
            var activeAfterAcquire = 0;
            var cpuPercent = 0d;
            var hasCpuSample = false;
            var stabilizationElapsed = false;

            lock (_admissionSync)
            {
                activeAfterAcquire = _activeLeases + 1;

                // Always admit one workflow so a busy machine cannot deadlock
                // the queue. Additional renderers require stable headroom.
                if (!_options.EnableAdaptiveCpuThrottle || activeAfterAcquire <= 1)
                {
                    admit = true;
                }
                else
                {
                    var elapsedMs = _lastAdmissionTimestamp == 0
                        ? double.MaxValue
                        : Stopwatch.GetElapsedTime(_lastAdmissionTimestamp).TotalMilliseconds;
                    stabilizationElapsed =
                        elapsedMs >= _options.BrowserAdmissionStabilizationMs;
                    hasCpuSample = _cpuSampler.TryReadPercent(out cpuPercent);
                    var cpuIsLow = hasCpuSample
                                   && cpuPercent < _options.MaxCpuPercentBeforeThrottling;

                    if (stabilizationElapsed && cpuIsLow)
                    {
                        _consecutiveLowSamples++;
                        admit =
                            _consecutiveLowSamples >= _options.CpuLowSamplesRequired;
                    }
                    else
                    {
                        _consecutiveLowSamples = 0;
                    }
                }

                if (admit)
                {
                    _activeLeases++;
                    _lastAdmissionTimestamp = Stopwatch.GetTimestamp();
                    _consecutiveLowSamples = 0;
                }
            }

            if (admit)
            {
                if (throttled)
                {
                    _logger.LogInformation(
                        "CPU governor admitted browser workflow after stable headroom. Active={Active}/{MaxActive}",
                        activeAfterAcquire,
                        _maxActiveSessions);
                }

                return new Lease(this);
            }

            _slots.Release();

            if (!throttled)
            {
                _logger.LogInformation(
                    "CPU governor is stabilizing browser admission. CpuSampleAvailable={CpuSampleAvailable}, CpuPercent={CpuPercent:0.0}, Threshold={Threshold:0.0}, StabilizationElapsed={StabilizationElapsed}",
                    hasCpuSample,
                    cpuPercent,
                    _options.MaxCpuPercentBeforeThrottling,
                    stabilizationElapsed);
                throttled = true;
            }

            Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
    }

    private void ReleaseLease()
    {
        lock (_admissionSync)
        {
            _activeLeases = Math.Max(0, _activeLeases - 1);
            _consecutiveLowSamples = 0;
        }

        // A lease can outlive host disposal by a few milliseconds while a
        // worker unwinds cancellation. Do not surface ObjectDisposedException
        // from that cleanup path.
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _slots.Dispose();
        }
    }

    private sealed class Lease : IBrowserConcurrencyLease
    {
        private AdaptiveBrowserConcurrencyGovernor? _owner;

        public Lease(AdaptiveBrowserConcurrencyGovernor owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseLease();
        }
    }

    private sealed class CpuSampler
    {
        private readonly object _sync = new();
        private ulong _previousIdle;
        private ulong _previousKernel;
        private ulong _previousUser;
        private bool _hasPrevious;

        public bool TryReadPercent(out double percent)
        {
            percent = 0;

            if (!OperatingSystem.IsWindows()
                || !GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return false;
            }

            var idleTicks = ToUInt64(idle);
            var kernelTicks = ToUInt64(kernel);
            var userTicks = ToUInt64(user);

            lock (_sync)
            {
                if (!_hasPrevious)
                {
                    _previousIdle = idleTicks;
                    _previousKernel = kernelTicks;
                    _previousUser = userTicks;
                    _hasPrevious = true;
                    return false;
                }

                var idleDelta = idleTicks - _previousIdle;
                var kernelDelta = kernelTicks - _previousKernel;
                var userDelta = userTicks - _previousUser;

                _previousIdle = idleTicks;
                _previousKernel = kernelTicks;
                _previousUser = userTicks;

                var total = kernelDelta + userDelta;
                if (total == 0)
                {
                    return false;
                }

                var busy = total > idleDelta ? total - idleDelta : 0;
                percent = Math.Clamp(busy * 100d / total, 0, 100);
                return true;
            }
        }

        private static ulong ToUInt64(FileTime fileTime) =>
            ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }
    }
}
