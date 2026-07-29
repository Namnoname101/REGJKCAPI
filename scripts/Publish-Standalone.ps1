[CmdletBinding()]
param(
    [string]$OutputPath
)

$projectPath = Join-Path $PSScriptRoot '..\BAOCODETOOL.csproj'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputPath = Join-Path $PSScriptRoot "..\artifacts\standalone-$timestamp"
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)

dotnet publish $projectPath --configuration Release --output $resolvedOutputPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-ChildItem -LiteralPath $resolvedOutputPath -File | Select-Object Name, Length
