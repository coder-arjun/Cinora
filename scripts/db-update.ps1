#Requires -Version 7.0
<#
.SYNOPSIS
    Applies EF Core migrations to the local SQL Server LocalDB database (Windows / free, no Docker).

.DESCRIPTION
    Runs `dotnet ef database update` against the local development database
    ((localdb)\MSSQLLocalDB, database CinoraDev), applying any pending migrations.

    The command is idempotent: re-running it when the database is already current applies nothing
    and reports "No migrations were applied. The database is already up to date." Safe to run before
    every dev session.

    Migrations live in Cinora.Infrastructure; the startup project is Cinora.Web (which supplies the
    design-time configuration). This mirrors the milestone 1.2 command.

.NOTES
    Requires the dotnet-ef global tool (install once with: dotnet tool install --global dotnet-ef).
    No Docker, no cloud, no git. In production, migrations are applied from a reviewed idempotent
    SQL script (dotnet ef migrations script --idempotent), never by this script and never on startup.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$infraProject = Join-Path $repoRoot 'src/Cinora.Infrastructure'
$webProject   = Join-Path $repoRoot 'src/Cinora.Web'

Write-Host 'Cinora - apply EF Core migrations to local SQL Server' -ForegroundColor Cyan
Write-Host '  Target : localhost, database CinoraDev'

# Verify the dotnet-ef tool is available and fail with an actionable message if it is not,
# rather than surfacing an opaque "No executable found matching command dotnet-ef".
dotnet ef --version 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "The 'dotnet-ef' tool is not installed. Install it with: dotnet tool install --global dotnet-ef"
}

Write-Host 'Applying migrations (idempotent)...' -ForegroundColor Cyan
dotnet ef database update --project $infraProject --startup-project $webProject
if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed (exit $LASTEXITCODE)." }

Write-Host 'Database is up to date.' -ForegroundColor Green
