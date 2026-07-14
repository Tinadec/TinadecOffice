# AI Tools Test Script for TinadecOffice
# This script tests the functionality of Ponytail and CodeGraph

Write-Host "🧪 Testing AI Tools for TinadecOffice..." -ForegroundColor Cyan
Write-Host ""

$testResults = @()
$passed = 0
$failed = 0
$warnings = 0

# Function to run a test
function Run-Test {
    param (
        [string]$TestName,
        [scriptblock]$TestScript,
        [bool]$Required = $true
    )

    Write-Host "Testing: $TestName" -ForegroundColor Yellow
    try {
        $result = & $TestScript
        if ($result -eq $true) {
            Write-Host "   ✅ PASSED" -ForegroundColor Green
            $script:passed++
            $script:testResults += @{Name=$TestName; Status="PASSED"; Required=$Required}
            return $true
        } else {
            if ($Required) {
                Write-Host "   ❌ FAILED" -ForegroundColor Red
                $script:failed++
                $script:testResults += @{Name=$TestName; Status="FAILED"; Required=$Required}
            } else {
                Write-Host "   ⚠️  WARNING" -ForegroundColor Yellow
                $script:warnings++
                $script:testResults += @{Name=$TestName; Status="WARNING"; Required=$Required}
            }
            return $false
        }
    } catch {
        if ($Required) {
            Write-Host "   ❌ FAILED: $_" -ForegroundColor Red
            $script:failed++
            $script:testResults += @{Name=$TestName; Status="FAILED"; Required=$Required}
        } else {
            Write-Host "   ⚠️  WARNING: $_" -ForegroundColor Yellow
            $script:warnings++
            $script:testResults += @{Name=$TestName; Status="WARNING"; Required=$Required}
        }
        return $false
    }
}

# Test 1: Ponytail Configuration Files
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📁 Test Suite: Ponytail Configuration" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

$projectPath = Split-Path -Parent $PSScriptRoot

Run-Test "Ponytail config.json exists" {
    Test-Path "$projectPath\.ponytail\config.json"
}

Run-Test "Ponytail rules.md exists" {
    Test-Path "$projectPath\.ponytail\rules.md"
}

Run-Test "Ponytail validate.js exists" {
    Test-Path "$projectPath\.ponytail\validate.js"
}

Run-Test "Ponytail config is valid JSON" {
    try {
        $config = Get-Content "$projectPath\.ponytail\config.json" | ConvertFrom-Json
        return ($config.project -eq "TinadecOffice")
    } catch {
        return $false
    }
}

Write-Host ""

# Test 2: CodeGraph Configuration Files
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📁 Test Suite: CodeGraph Configuration" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Run-Test "CodeGraph config.json exists" {
    Test-Path "$projectPath\.codegraph\config.json"
}

Run-Test "CodeGraph mcp.json exists" {
    Test-Path "$projectPath\.codegraph\mcp.json"
}

Run-Test "CodeGraph config is valid JSON" {
    try {
        $config = Get-Content "$projectPath\.codegraph\config.json" | ConvertFrom-Json
        return ($config.project -eq "TinadecOffice")
    } catch {
        return $false
    }
}

Run-Test "CodeGraph MCP config is valid JSON" {
    try {
        $config = Get-Content "$projectPath\.codegraph\mcp.json" | ConvertFrom-Json
        return ($config.mcpServers.codegraph.command -eq "codegraph")
    } catch {
        return $false
    }
}

Write-Host ""

# Test 3: AGENTS.md Integration
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📄 Test Suite: AGENTS.md Integration" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Run-Test "AGENTS.md contains Ponytail rules" {
    $content = Get-Content "$projectPath\AGENTS.md" -Raw
    return ($content -match "PONYTAIL CODING PRINCIPLES")
}

Run-Test "AGENTS.md contains CodeGraph integration" {
    $content = Get-Content "$projectPath\AGENTS.md" -Raw
    return ($content -match "CodeGraph Integration")
}

Run-Test "AGENTS.md contains safety rules" {
    $content = Get-Content "$projectPath\AGENTS.md" -Raw
    return ($content -match "NEVER remove validation")
}

Write-Host ""

# Test 4: CLAUDE.md Integration
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📄 Test Suite: CLAUDE.md Integration" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Run-Test "CLAUDE.md contains Ponytail integration" {
    $content = Get-Content "$projectPath\CLAUDE.md" -Raw
    return ($content -match "Ponytail Integration")
}

Run-Test "CLAUDE.md contains CodeGraph integration" {
    $content = Get-Content "$projectPath\CLAUDE.md" -Raw
    return ($content -match "CodeGraph Integration")
}

Run-Test "CLAUDE.md contains verification commands" {
    $content = Get-Content "$projectPath\CLAUDE.md" -Raw
    return ($content -match "npm run ai:tools:check")
}

Write-Host ""

# Test 5: package.json Scripts
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📦 Test Suite: package.json Scripts" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Run-Test "package.json contains AI tool scripts" {
    $content = Get-Content "$projectPath\package.json" -Raw
    return ($content -match "ai:codegraph:init" -and $content -match "ai:ponytail:validate")
}

Run-Test "package.json contains tools:check script" {
    $content = Get-Content "$projectPath\package.json" -Raw
    return ($content -match "ai:tools:check")
}

Write-Host ""

# Test 6: Documentation Files
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📚 Test Suite: Documentation" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Run-Test "AI tools integration guide exists" {
    Test-Path "$projectPath\docs\ai-tools-integration-guide.md"
}

Run-Test "AI tools quick start guide exists" {
    Test-Path "$projectPath\docs\ai-tools-quick-start.md"
}

Run-Test "Architecture compliance verification exists" {
    Test-Path "$projectPath\docs\architecture-compliance-verification.md"
}

Write-Host ""

# Test 7: Architecture Compliance
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "🏗️  Test Suite: Architecture Compliance" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Run-Test "Desktop layer path exists" {
    Test-Path "$projectPath\apps\desktop"
}

Run-Test "Gateway layer path exists" {
    Test-Path "$projectPath\gateway"
}

Run-Test "Core layer path exists" {
    Test-Path "$projectPath\src\TinadecCore"
}

Write-Host ""

# Summary
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "📊 Test Summary" -ForegroundColor Cyan
Write-Host "=" * 60 -ForegroundColor Cyan

Write-Host ""
Write-Host "Total Tests: $($testResults.Count)" -ForegroundColor White
Write-Host "Passed:      $passed" -ForegroundColor Green
Write-Host "Failed:      $failed" -ForegroundColor Red
Write-Host "Warnings:    $warnings" -ForegroundColor Yellow
Write-Host ""

if ($failed -eq 0) {
    Write-Host "🎉 All tests passed!" -ForegroundColor Green
    Write-Host ""
    Write-Host "✅ AI Tools Integration Status: READY" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next Steps:" -ForegroundColor White
    Write-Host "  1. Run 'npm run ai:tools:install' to install CodeGraph" -ForegroundColor Gray
    Write-Host "  2. Run 'npm run ai:tools:check' to verify configuration" -ForegroundColor Gray
    Write-Host "  3. Restart your AI tools" -ForegroundColor Gray
    Write-Host "  4. Refer to docs/ai-tools-quick-start.md for usage" -ForegroundColor Gray
} else {
    Write-Host "❌ Some tests failed" -ForegroundColor Red
    Write-Host ""
    Write-Host "Failed Tests:" -ForegroundColor Red
    $testResults | Where-Object { $_.Status -eq "FAILED" } | ForEach-Object {
        Write-Host "  - $($_.Name)" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "Please fix the failed tests before proceeding" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Test completed at: $(Get-Date)" -ForegroundColor Gray
