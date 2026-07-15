#Requires -Version 7.0
<#
.SYNOPSIS
    Cinora local development loop (Windows / free, no Docker).

.DESCRIPTION
    Restores and builds the solution, starts the Tailwind v4 + TypeScript watcher
    (`npm run watch`) in the background so CSS/JS rebuild on save, then runs the ASP.NET Core
    app under `dotnet watch run` for C#/Razor hot reload.

    The frontend watcher runs as a PowerShell background job named 'cinora-frontend-watch'.
    It is stopped automatically when this script exits (including Ctrl+C, which stops
    `dotnet watch`). If the script is killed abruptly, stop it manually with:

        Stop-Job  -Name cinora-frontend-watch
        Remove-Job -Name cinora-frontend-watch

    View the watcher's output at any time with:

        Receive-Job -Name cinora-frontend-watch -Keep

.NOTES
    No Docker, no cloud, no git. Prerequisites: .NET 10 SDK, Node.js 20+, SQL Server LocalDB.
    Run `scripts/db-update.ps1` once first to apply migrations to the local database.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$solution   = Join-Path $repoRoot 'Cinora.sln'
$webProject = Join-Path $repoRoot 'src/Cinora.Web'
$watchName  = 'cinora-frontend-watch'

Write-Host 'Cinora - local development loop' -ForegroundColor Cyan
Write-Host "  Repo root : $repoRoot"

# 1. Restore + build once up front so the first `dotnet watch` iteration is fast and any
#    compile error surfaces immediately rather than mid-hot-reload.
Write-Host ''
Write-Host 'Restoring packages...' -ForegroundColor Cyan
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)." }

Write-Host 'Building solution (Debug)...' -ForegroundColor Cyan
dotnet build $solution -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }

# 2. Clean up any watcher left over from a previous run, then start a fresh one in the
#    background. `npm run watch --prefix <web>` runs Tailwind + esbuild in --watch mode.
Get-Job -Name $watchName -ErrorAction SilentlyContinue | Remove-Job -Force

Write-Host ''
Write-Host 'Starting frontend watcher (Tailwind v4 + TypeScript)...' -ForegroundColor Cyan
$watchJob = Start-Job -Name $watchName -ScriptBlock {
    param($web)
    npm run watch --prefix $web
} -ArgumentList $webProject

Write-Host "  Watcher running as background job '$($watchJob.Name)' (Id $($watchJob.Id))."
Write-Host "  Output : Receive-Job -Name $watchName -Keep"
Write-Host "  Stop   : Stop-Job -Name $watchName; Remove-Job -Name $watchName"

# 3. Print the app URLs (from Properties/launchSettings.json, https profile).
Write-Host ''
Write-Host 'App URLs (Development):' -ForegroundColor Cyan
Write-Host '  https://localhost:7149'
Write-Host '  http://localhost:5148'
Write-Host '  Health : http://localhost:5148/health'
Write-Host ''
Write-Host 'Starting dotnet watch (Ctrl+C to stop both the app and the watcher)...' -ForegroundColor Cyan

# 4. Run the app with hot reload in the foreground. When it exits (Ctrl+C or crash) the
#    finally block tears the watcher down so no orphaned npm process is left behind.
try {
    dotnet watch run --project $webProject
}
finally {
    $job = Get-Job -Name $watchName -ErrorAction SilentlyContinue
    if ($null -ne $job) {
        Stop-Job  -Name $watchName -ErrorAction SilentlyContinue
        Remove-Job -Name $watchName -Force -ErrorAction SilentlyContinue
        Write-Host 'Frontend watcher stopped.' -ForegroundColor Cyan
    }
}
