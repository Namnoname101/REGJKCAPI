# BAOCODETOOL Console Worker

Project now runs as a .NET Generic Host from a terminal. The WPF source files are
kept in the repository for reference, but they are excluded from the console
build and are no longer required to keep the Telegram bot alive.

## Configure

Do not commit the Telegram bot token. Configure it with an environment variable:

```powershell
$env:Telegram__BotToken = 'YOUR_TELEGRAM_BOT_TOKEN'
```

The admin chat ID and worker concurrency are configured in `appsettings.json`:

```json
{
  "Telegram": {
    "BotToken": "",
    "AdminChatId": 1853328773
  },
  "Automation": {
    "MaxConcurrentJobs": 1,
    "MaxBrowserThreadsPerJob": 10,
    "MaxConcurrentSitesPerAccount": 1,
    "MaxConcurrentBrowserStarts": 1,
    "QueueCapacity": 100,
    "EnablePrewarmedBrowserPool": true,
    "PrewarmedBrowserPoolSize": 2,
    "BrowserPoolMaxUsesPerSession": 20,
    "MaxActiveBrowserSessions": 1,
    "MaxCpuPercentBeforeThrottling": 75,
    "EnableAdaptiveCpuThrottle": true,
    "BrowserAdmissionStabilizationMs": 2000,
    "CpuLowSamplesRequired": 3,
    "Headless": false,
    "AllowDirectConnectionForTesting": true
  }
}
```

An environment variable overrides the corresponding JSON value. For example:

```powershell
$env:Automation__MaxConcurrentJobs = '3'
```

`MaxConcurrentBrowserStarts` limits only the expensive Chrome creation phase.
Already-running sessions continue working while another session is starting, so
one burst of new registrations cannot starve all existing tabs. Start with `1`
or `2`, then increase only after measuring CPU/RAM on the deployment machine.

`MaxConcurrentSitesPerAccount` controls how many selected sites for one account
run at the same time. The default `1` is tuned for the current low-end
2-core/4-thread machine and keeps three selected sites from starving one
another. Set it to `2` or `3` only after a burst benchmark on a stronger host.

`QueueCapacity` bounds the number of jobs waiting in memory. When the queue is
full, Telegram producers wait for a worker to free a slot instead of allowing
unbounded memory growth or silently dropping registrations.

When `EnablePrewarmedBrowserPool` is enabled, the host opens
`PrewarmedBrowserPoolSize` Chrome sessions before Telegram polling starts.
Direct-connection jobs rent one session, run one site, then return it after
cookies, cache, local/session storage, IndexedDB, service workers and extra tabs
are cleared. Sessions are recreated after `BrowserPoolMaxUsesPerSession` uses.
Jobs with a proxy or custom user-agent automatically use a dedicated browser
because those settings are fixed when Chrome starts.

Telegram-created accounts use a short username (`u` plus seven random digits,
8 characters total). If a site reports that the username already exists, the
retry generates another username with the same short format.

`MaxActiveBrowserSessions` is the process-wide safety cap. The pool keeps one
extra blank slot prewarmed, while only the configured number of active
 workflows consumes CPU. With `EnableAdaptiveCpuThrottle`, a new workflow is
admitted only after `BrowserAdmissionStabilizationMs` has elapsed and
`CpuLowSamplesRequired` consecutive samples remain below
`MaxCpuPercentBeforeThrottling`. This prevents a brief idle dip immediately
after the first Chrome starts from releasing a second renderer that would push
both tabs into CPU starvation. If CPU stays high, the current workflow finishes
and the next queued job starts immediately.

`Headless` is an opt-in resource reduction for server runs that do not need to
see Chrome windows. Test it per site before enabling it in production because
some anti-bot flows behave differently in headless mode.

After registration, supported member-center pages use an authenticated
same-session bank-binding fast path. It reuses the page's own store and
encrypted request code, so the bank data can be submitted without opening the
add-bank route or waiting for its form to render. Banks that require OTP,
province/city selection, custom fields, GA confirmation, or an unknown template
automatically use the existing Selenium flow. This keeps correctness ahead of
speed when a site changes its form.

The release diagnostics are available under `diagnostics/tools`:

```powershell
dotnet run --project .\diagnostics\tools\BrowserPoolDiagnostics\BrowserPoolDiagnostics.csproj -c Release
dotnet run --project .\diagnostics\tools\QueueDiagnostics\QueueDiagnostics.csproj -c Release
dotnet run --project .\diagnostics\tools\LiveRegistrationBenchmark\LiveRegistrationBenchmark.csproj -c Release --no-build RR88
dotnet run --project .\diagnostics\tools\LiveConcurrencyBenchmark\LiveConcurrencyBenchmark.csproj -c Release --no-build -- RR88 2
```

The live benchmark uses synthetic test data and appends phase timings to
`bin\Release\net8.0-windows\metrics\site-performance.jsonl`. On the low-end
test machine, the authenticated bank phase measured about 1.5 seconds after
the fast path was enabled; the previous visual route measured about 30–32
seconds.

For this machine, a two-account burst benchmark completed in about 45 seconds
with `MaxActiveBrowserSessions=1`. Allowing two active renderers increased the
same workload to roughly 124–185 seconds because both Chrome instances
starved each other and the member-center APIs slowed down. The default profile
therefore queues users while keeping two sessions prewarmed. If an operator
deploys on a stronger host, the burst diagnostic accepts a third argument to
test a higher active-session cap.

The fast API path records whether submission is still in flight. If Selenium
times out after the server may have accepted the request, it verifies the
member's bound-card list and never blindly resubmits through the visual form.

`QueueDiagnostics` verifies bounded-channel backpressure, cancellation while a
producer is blocked on a full queue, and idempotent job completion. During host
shutdown, jobs already accepted into the queue are marked failed and persisted
instead of being silently left in `Pending`.

When `AllowDirectConnectionForTesting` is `true`, Telegram accepts:

- single-account flow without asking for a proxy;
- bulk rows with either four fields (`HOTEN|TENNGANHANG|SDT|SOTK`) or the original five fields including proxy.

Set it to `false` when every request must provide a proxy again.

## Run

```powershell
cd E:\Code\VibecodeReg
dotnet restore
dotnet run
```

Stop cleanly with `Ctrl+C`.

For a release executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The executable is written below `bin\Release\net8.0-windows\win-x64\publish`.

## Runtime pipeline

1. `TelegramBotService` keeps Telegram long polling alive.
2. `BotService` validates the existing single/bulk input flows and creates jobs.
3. `JobQueueService` assigns `Pending`, `Processing`, `Success`, or `Failed`.
4. `JobWorkerService` runs the configured number of queue consumers.
5. `RegistrationOrchestrator` calls `BrowserAutomationService`.
6. `BrowserAutomationService` calls the existing `BotManager`, `DriverFactory`,
   and site strategies without rewriting their registration behavior.
7. `ResultSenderService` edits the progress message and sends the final Telegram
   result.

Chrome profiles and proxy relays remain isolated per account/site. The service
does not reuse one browser between unrelated users because that would risk
leaking cookies, local storage, proxy identity, or an authenticated session.

The queue is in memory. A process restart stops active jobs and discards jobs
that have not started.
