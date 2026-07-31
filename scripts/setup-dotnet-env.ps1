# dotnet environment setup wrapper for TinadecOffice
# Clears Version/Ice-Version from the environment (they break child dotnet
# processes on this machine), prints diagnostics, then runs `dotnet <args>`.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 <dotnet args...>

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotnetArgs
)

$ErrorActionPreference = 'Stop'

Write-Host "[setup-dotnet-env] Preparing environment for dotnet..." -ForegroundColor Cyan

# Clear environment variables known to confuse child dotnet processes
foreach ($name in @('Version', 'Ice-Version')) {
    $envPath = "Env:$name"
    if (Test-Path -LiteralPath $envPath) {
        $value = (Get-Item -LiteralPath $envPath).Value
        Remove-Item -LiteralPath $envPath
        Write-Host "[setup-dotnet-env] Cleared $name (was: '$value')" -ForegroundColor Yellow
    } else {
        Write-Host "[setup-dotnet-env] $name not set, nothing to clear" -ForegroundColor Gray
    }
}

# Verify dotnet is available before invoking it
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "[setup-dotnet-env] ERROR: 'dotnet' not found on PATH. Install the .NET SDK (net10.0) or fix PATH." -ForegroundColor Red
    exit 1
}
Write-Host "[setup-dotnet-env] dotnet: $($dotnetCmd.Source)" -ForegroundColor Gray

if (-not $DotnetArgs -or $DotnetArgs.Count -eq 0) {
    Write-Host "[setup-dotnet-env] ERROR: No dotnet arguments provided. Usage: setup-dotnet-env.ps1 <dotnet args...>" -ForegroundColor Red
    exit 1
}

Write-Host "[setup-dotnet-env] Running: dotnet $($DotnetArgs -join ' ')" -ForegroundColor Cyan
& dotnet @DotnetArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host "[setup-dotnet-env] ERROR: 'dotnet $($DotnetArgs -join ' ')' exited with code $exitCode (cwd: $(Get-Location))" -ForegroundColor Red
}

exit $exitCode
