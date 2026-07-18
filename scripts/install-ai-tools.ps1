# AI Tools Installation Script for TinadecOffice
# This script installs and configures Ponytail and CodeGraph

Write-Host "🚀 Installing AI Tools for TinadecOffice..." -ForegroundColor Cyan
Write-Host ""

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "⚠️  Warning: Running without administrator privileges" -ForegroundColor Yellow
    Write-Host "   Some installations may require elevated permissions" -ForegroundColor Yellow
    Write-Host ""
}

# Function to check if a command exists
function Test-Command {
    param ($command)
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'stop'
    try {
        if(Get-Command $command){return $true}
    } catch {return $false}
    finally {$ErrorActionPreference=$oldPreference}
}

# Step 1: Install CodeGraph
Write-Host "📦 Step 1: Installing CodeGraph..." -ForegroundColor Green

if (Test-Command "codegraph") {
    Write-Host "   ✅ CodeGraph is already installed" -ForegroundColor Green
    $codegraphVersion = & codegraph --version
    Write-Host "   Version: $codegraphVersion" -ForegroundColor Gray
} else {
    Write-Host "   Installing CodeGraph via npm..." -ForegroundColor Yellow
    try {
        npm i -g @colbymchenry/codegraph
        if ($LASTEXITCODE -eq 0) {
            Write-Host "   ✅ CodeGraph installed successfully" -ForegroundColor Green
        } else {
            Write-Host "   ❌ Failed to install CodeGraph" -ForegroundColor Red
            Write-Host "   Please install manually: npm i -g @colbymchenry/codegraph" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "   ❌ Error installing CodeGraph: $_" -ForegroundColor Red
    }
}

Write-Host ""

# Step 2: Initialize CodeGraph for the project
Write-Host "🔧 Step 2: Initializing CodeGraph for project..." -ForegroundColor Green

if (Test-Command "codegraph") {
    $projectPath = Split-Path -Parent $PSScriptRoot
    Write-Host "   Project path: $projectPath" -ForegroundColor Gray

    # Check if already initialized
    Push-Location $projectPath
    try {
        & codegraph status *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "   ✅ CodeGraph index is already initialized" -ForegroundColor Green
        } else {
            Write-Host "   Initializing CodeGraph index..." -ForegroundColor Yellow
            & codegraph init
            if ($LASTEXITCODE -eq 0) {
                Write-Host "   ✅ CodeGraph initialized successfully" -ForegroundColor Green
            } else {
                Write-Host "   ⚠️  CodeGraph initialization completed with warnings" -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host "   ❌ Error initializing CodeGraph: $_" -ForegroundColor Red
    } finally {
        Pop-Location
    }
} else {
    Write-Host "   ⚠️  Skipping - CodeGraph not installed" -ForegroundColor Yellow
}

Write-Host ""

# Step 3: Configure AI tool integration
Write-Host "🔗 Step 3: Configuring AI tool integration..." -ForegroundColor Green

if (Test-Command "codegraph") {
    $projectPath = Split-Path -Parent $PSScriptRoot
    Push-Location $projectPath
    try {
        Write-Host "   Configuring OpenCode integration..." -ForegroundColor Yellow
        & codegraph telemetry off
        & codegraph install --target=opencode --location=local --yes
        Write-Host "   ✅ OpenCode integration configured" -ForegroundColor Green
    } catch {
        Write-Host "   ⚠️  Some integrations may need manual configuration" -ForegroundColor Yellow
    } finally {
        Pop-Location
    }
} else {
    Write-Host "   ⚠️  Skipping - CodeGraph not installed" -ForegroundColor Yellow
}

Write-Host ""

# Step 4: Validate Ponytail configuration
Write-Host "✅ Step 4: Validating Ponytail configuration..." -ForegroundColor Green

$projectPath = Split-Path -Parent $PSScriptRoot
$ponytailConfig = "$projectPath\.ponytail\config.json"
$ponytailRules = "$projectPath\.ponytail\rules.md"

if ((Test-Path $ponytailConfig) -and (Test-Path $ponytailRules)) {
    Write-Host "   ✅ Ponytail configuration files present" -ForegroundColor Green

    # Run validation script
    $validateScript = "$projectPath\.ponytail\validate.js"
    if (Test-Path $validateScript) {
        Write-Host "   Running validation script..." -ForegroundColor Yellow
        Push-Location $projectPath
        try {
            & node $validateScript
        } catch {
            Write-Host "   ⚠️  Validation script encountered issues" -ForegroundColor Yellow
        } finally {
            Pop-Location
        }
    }
} else {
    Write-Host "   ❌ Ponytail configuration files missing" -ForegroundColor Red
    Write-Host "   Please run the setup again" -ForegroundColor Yellow
}

Write-Host ""

# Step 5: Verify installation
Write-Host "🔍 Step 5: Verifying installation..." -ForegroundColor Green

$checks = @(
    @{Name="CodeGraph CLI"; Command="codegraph"; Required=$false},
    @{Name="Node.js"; Command="node"; Required=$true},
    @{Name="npm"; Command="npm"; Required=$true}
)

$allPassed = $true
foreach ($check in $checks) {
    if (Test-Command $check.Command) {
        Write-Host "   ✅ $($check.Name) is available" -ForegroundColor Green
    } else {
        if ($check.Required) {
            Write-Host "   ❌ $($check.Name) is required but not found" -ForegroundColor Red
            $allPassed = $false
        } else {
            Write-Host "   ⚠️  $($check.Name) is not installed (optional)" -ForegroundColor Yellow
        }
    }
}

Write-Host ""

# Summary
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📊 Installation Summary" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

if ($allPassed) {
    Write-Host "✅ All required tools are installed" -ForegroundColor Green
} else {
    Write-Host "⚠️  Some required tools are missing" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "📋 Next Steps:" -ForegroundColor White
Write-Host "   1. Restart your AI tools (Claude Code, OpenCode)" -ForegroundColor Gray
Write-Host "   2. Run 'npm run ai:tools:check' to verify configuration" -ForegroundColor Gray
Write-Host "   3. Refer to docs/ai-tools-quick-start.md for usage guide" -ForegroundColor Gray
Write-Host ""

Write-Host "📚 Documentation:" -ForegroundColor White
Write-Host "   - AI Tools Integration Guide: docs/ai-tools-integration-guide.md" -ForegroundColor Gray
Write-Host "   - Quick Start Guide: docs/ai-tools-quick-start.md" -ForegroundColor Gray
Write-Host "   - Architecture Compliance: docs/architecture-compliance-verification.md" -ForegroundColor Gray
Write-Host ""

Write-Host "🎉 Installation complete!" -ForegroundColor Green
