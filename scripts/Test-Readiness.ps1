[CmdletBinding()]
param(
    [string]$ProjectRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

function New-CheckResult {
    param(
        [ValidateSet('PASS', 'WARN', 'FAIL')][string]$Status,
        [string]$Check,
        [string]$Detail
    )

    [pscustomobject]@{
        Status = $Status
        Check  = $Check
        Detail = $Detail
    }
}

function Get-JsonValue {
    param(
        [object]$Root,
        [string[]]$Path
    )

    $current = $Root
    foreach ($segment in $Path) {
        if ($null -eq $current) {
            return $null
        }

        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $null
        }

        $current = $property.Value
    }

    return [string]$current
}

function Test-LoopbackUrl {
    param([string]$Url)

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return $false
    }

    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    return $uri.Host -in @('localhost', '127.0.0.1', '::1')
}

$results = [System.Collections.Generic.List[object]]::new()
$projectFile = Join-Path $ProjectRoot 'BAOCODETOOL.csproj'
$settingsFile = Join-Path $ProjectRoot 'appsettings.json'

if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "BAOCODETOOL.csproj was not found beneath '$ProjectRoot'."
}

$results.Add((New-CheckResult 'PASS' 'Project root' 'Project file was found.'))

$settings = $null
if (-not (Test-Path -LiteralPath $settingsFile)) {
    $results.Add((New-CheckResult 'WARN' 'Configuration file' 'appsettings.json is absent. The standalone build must use LocalAppData settings or environment variables.'))
}
else {
    try {
        $settings = (Get-Content -LiteralPath $settingsFile -Raw) | ConvertFrom-Json
        $results.Add((New-CheckResult 'PASS' 'Configuration syntax' 'appsettings.json contains valid JSON.'))
    }
    catch {
        $results.Add((New-CheckResult 'FAIL' 'Configuration syntax' 'appsettings.json is not valid JSON.'))
    }
}

if ($null -ne $settings) {
$localUrl = Get-JsonValue $settings @('Hosting', 'LocalUrl')
    if (Test-LoopbackUrl $localUrl) {
        $results.Add((New-CheckResult 'PASS' 'Dashboard binding' 'The configured dashboard URL is loopback-only.'))
    }
    else {
        $results.Add((New-CheckResult 'FAIL' 'Dashboard binding' 'Configure Hosting:LocalUrl with localhost, 127.0.0.1, or ::1; do not expose the dashboard directly.'))
    }

$secretPaths = @(
        @('Telegram', 'BotToken'),
        @('CaptchaSolver', 'ApiKey'),
        @('OmoCaptcha', 'ApiKey'),
        @('ProxyVn', 'ApiKey'),
        @('SePay', 'WebhookSecret'),
        @('SePay', 'GatewayAccessToken')
    )
    $configuredSecrets = @($secretPaths | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-JsonValue $settings $_))
    }).Count

    if ($configuredSecrets -eq 0) {
        $results.Add((New-CheckResult 'PASS' 'Repository secrets' 'No configured secret was found in the tracked settings file.'))
    }
    else {
        $results.Add((New-CheckResult 'FAIL' 'Repository secrets' "$configuredSecrets secret setting(s) are populated in appsettings.json. Rotate them externally, then move them to user/machine environment variables or the LocalAppData settings file."))
    }
}

try {
    $trackedSettings = @(git -C $ProjectRoot ls-files -- appsettings.json Configuration/settings.template.json)
    if ($trackedSettings.Count -gt 0) {
        $results.Add((New-CheckResult 'WARN' 'Tracked configuration' 'Configuration templates are tracked. Keep them free of real credentials.'))
    }

    $changes = @(git -C $ProjectRoot status --short)
    if ($changes.Count -eq 0) {
        $results.Add((New-CheckResult 'PASS' 'Git worktree' 'The worktree is clean.'))
    }
    else {
        $results.Add((New-CheckResult 'WARN' 'Git worktree' "$($changes.Count) uncommitted change(s) exist. Review and commit them before a release."))
    }
}
catch {
    $results.Add((New-CheckResult 'WARN' 'Git metadata' 'Git checks were skipped because Git is unavailable.'))
}

$database = Join-Path $ProjectRoot 'vibecodereg.db'
if (Test-Path -LiteralPath $database) {
    $size = (Get-Item -LiteralPath $database).Length
    if ($size -gt 0) {
        $results.Add((New-CheckResult 'WARN' 'Local database' 'A local SQLite database exists. Back it up only while the application is stopped, or use SQLite online-backup tooling.'))
    }
}

$results | Format-Table -AutoSize

if ($results.Status -contains 'FAIL') {
    exit 1
}
