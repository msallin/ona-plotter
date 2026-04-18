#!/usr/bin/env pwsh
# Builds OnaPlotter as a SignalK webapp and deploys it to pi@openplotter.local.
#
# Usage:
#   pwsh ./deploy/deploy.ps1                # default target: pi@openplotter.local
#   pwsh ./deploy/deploy.ps1 -Host user@host # custom target
#   pwsh ./deploy/deploy.ps1 -SkipBuild     # skip dotnet publish (reuse prior build)

param(
    [string]$SshTarget = "pi@openplotter.local",
    [string]$WebappName = "signalk-onaplotter",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to repo root.
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot "OnaPlotter/OnaPlotter.csproj"
$PublishDir = Join-Path $RepoRoot "deploy/publish"
$WwwRootSrc = Join-Path $PublishDir "wwwroot"
$StagingDir = Join-Path $RepoRoot "deploy/staging"
$PackageJsonTemplate = Join-Path $PSScriptRoot "package.json.template"

# 1. Build & publish Release.
if (-not $SkipBuild) {
    Write-Host "Publishing Release build..." -ForegroundColor Cyan

    # WASM AOT sometimes leaves a half-written PE image in obj/ after a prior
    # aborted publish, which then fails the next run with "PE image does not
    # have metadata". Nuking obj/Release and bin/Release avoids this without
    # touching the Debug cache we use for dotnet run.
    $ProjectDir = Split-Path -Parent $ProjectPath
    $ObjRelease = Join-Path $ProjectDir "obj/Release"
    $BinRelease = Join-Path $ProjectDir "bin/Release"
    if (Test-Path $ObjRelease) { Remove-Item -Recurse -Force $ObjRelease }
    if (Test-Path $BinRelease) { Remove-Item -Recurse -Force $BinRelease }
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

    dotnet publish $ProjectPath -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if (-not (Test-Path $WwwRootSrc)) {
    throw "Expected publish output at $WwwRootSrc but it does not exist."
}

# 2. Patch index.html <base href="/"> to "/{WebappName}/" for subpath serving.
$IndexHtml = Join-Path $WwwRootSrc "index.html"
$content = Get-Content $IndexHtml -Raw
$patched = $content -replace '<base href="/" />', "<base href=""/$WebappName/"" />"
Set-Content -Path $IndexHtml -Value $patched -NoNewline
Write-Host "Patched <base href> to /$WebappName/" -ForegroundColor Green

# 2b. Set appsettings.json ServerUrl to "auto" so the deployed app uses the
#     page origin (the SignalK server hosting it), not a hardcoded dev URL.
$AppSettings = Join-Path $WwwRootSrc "appsettings.json"
Set-Content -Path $AppSettings -Value '{ "SignalK": { "ServerUrl": "auto" } }'
Write-Host "Set ServerUrl to 'auto' for deployed build" -ForegroundColor Green

# 3. Prepare staging folder with SignalK webapp layout:
#    signalk-onaplotter/
#      package.json
#      public/  <- wwwroot contents
Write-Host "Staging SignalK webapp bundle..." -ForegroundColor Cyan
if (Test-Path $StagingDir) { Remove-Item -Recurse -Force $StagingDir }
$WebappStaging = Join-Path $StagingDir $WebappName
New-Item -ItemType Directory -Path $WebappStaging -Force | Out-Null

# Read version from csproj (fallback to timestamp).
$csproj = Get-Content $ProjectPath -Raw
$versionMatch = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
$version = if ($versionMatch.Success) { $versionMatch.Groups[1].Value } else { (Get-Date -Format "1.0.yyyyMMdd.HHmm") }

# Write package.json from template.
$pkgJson = Get-Content $PackageJsonTemplate -Raw
$pkgJson = $pkgJson -replace '__VERSION__', $version
Set-Content -Path (Join-Path $WebappStaging "package.json") -Value $pkgJson

# Copy wwwroot -> public/
Copy-Item -Recurse -Path $WwwRootSrc -Destination (Join-Path $WebappStaging "public")

Write-Host "Staged at $WebappStaging" -ForegroundColor Green

# 4. SCP to SignalK node_modules.
# ~ is expanded by the remote shell; quoted so PowerShell leaves it alone.
$RemotePath = "~/.signalk/node_modules/$WebappName"
Write-Host "Deploying to $SshTarget : $RemotePath ..." -ForegroundColor Cyan

# Verify we can reach the host and that ~/.signalk exists. A password
# prompt here is fine - we just want the whole script to stop on the
# probe rather than halfway through with a cryptic "mkdir failed".
# (No BatchMode=yes: that would refuse to prompt for a password, which
# makes the probe fail on a Pi that still uses password auth.)
Write-Host "Testing SSH connection (may prompt for password)..." -ForegroundColor DarkGray
$sshTest = ssh -o ConnectTimeout=10 $SshTarget 'test -d ~/.signalk && echo OK' 2>&1
if ($LASTEXITCODE -ne 0 -or $sshTest -notmatch "OK") {
    Write-Host $sshTest -ForegroundColor Yellow
    throw "SSH to $SshTarget failed. Check: (1) you can 'ssh $SshTarget' manually, (2) ~/.signalk exists on the target, (3) ssh-copy-id if you'd like password-less deploys."
}

# Create remote directory (wipe old install). Keep the two ops separate so
# the exit code pinpoints which one failed.
$rmOut = ssh $SshTarget "rm -rf '$RemotePath'" 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host $rmOut -ForegroundColor Yellow; throw "Remote rm failed (is the current install owned by another user?)" }
$mkOut = ssh $SshTarget "mkdir -p '$RemotePath'" 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host $mkOut -ForegroundColor Yellow; throw "Remote mkdir failed" }

# Copy staging contents.
# Use scp -r for the whole folder; rsync would be nicer but might not be installed.
scp -r "$WebappStaging/*" "${SshTarget}:$RemotePath/"
if ($LASTEXITCODE -ne 0) { throw "scp failed (check disk space on target with 'df -h ~')" }

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  Deployed OnaPlotter v$version" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "To finish:"
Write-Host "  1. Restart SignalK server:"
Write-Host "     ssh $SshTarget 'sudo systemctl restart signalk'"
Write-Host "     (or via the OpenPlotter UI)"
Write-Host ""
Write-Host "  2. Open: http://openplotter.local:3000/$WebappName/"
Write-Host "     (or from the SignalK Webapps admin page)"
Write-Host ""
