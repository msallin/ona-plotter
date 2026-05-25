#!/usr/bin/env pwsh
# Builds OnaPlotter as a SignalK webapp and deploys it to pi@home.ona.
#
# Usage:
#   pwsh ./deploy/deploy.ps1                # default target: pi@home.ona
#   pwsh ./deploy/deploy.ps1 -Host user@host # custom target
#   pwsh ./deploy/deploy.ps1 -SkipBuild     # skip dotnet publish (reuse prior build)

param(
    [string]$SshTarget = "pi@home.ona",
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

    # Ensure the JS toolchain is installed. The MinifyPublishedJs target
    # in OnaPlotter.csproj invokes esbuild on Release publish and the
    # CheckEsbuildPresent guard fails the publish with a clear MSBuild
    # error if node_modules is missing. npm install (incremental) is
    # cheap when the tree is already populated, so a fresh-clone deploy
    # works without manual setup.
    $NodeModulesDir = Join-Path $RepoRoot "node_modules"
    if (-not (Test-Path $NodeModulesDir)) {
        Write-Host "Installing JS toolchain (one-time)..." -ForegroundColor Cyan
        Push-Location $RepoRoot
        try {
            npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        } finally {
            Pop-Location
        }
    }

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

# Copy index.js (the plugin shim that adds the SPA fallback so
# Blazor client-routed URLs like /signalk-onaplotter/map return
# index.html on reload instead of 404). The file is small and
# dep-free; sits next to package.json at the package root.
$PluginShim = Join-Path $PSScriptRoot "index.js"
if (-not (Test-Path $PluginShim)) {
    throw "Expected plugin shim at $PluginShim - deploy/index.js is required."
}
Copy-Item -Path $PluginShim -Destination (Join-Path $WebappStaging "index.js")

# Copy wwwroot -> public/
Copy-Item -Recurse -Path $WwwRootSrc -Destination (Join-Path $WebappStaging "public")

Write-Host "Staged at $WebappStaging" -ForegroundColor Green

# 4. SCP to SignalK node_modules.
# ~ is expanded by the remote shell; quoted so PowerShell leaves it alone.
$RemotePath = "~/.signalk/node_modules/$WebappName"
Write-Host "Deploying to $SshTarget : $RemotePath ..." -ForegroundColor Cyan

# Password-prompt floor without SSH keys is TWO: one ssh for setup
# (probe + rm + mkdir consolidated into a single remote shell) and
# one scp for the upload. The previous ControlMaster attempt was
# supposed to collapse both into one prompt, but Microsoft's Windows
# OpenSSH doesn't expand the %r@%h-%p tokens in ControlPath
# reliably - the socket file fails to bind, ssh silently falls
# back to BatchMode-ish behaviour, and the helm sees a script that
# "doesn't ask for a password" before exiting with auth failure.
# Keeping it simple here: bare ssh + bare scp. For a one-prompt
# deploy, set up key auth:
#
#   ssh-copy-id $SshTarget
#
# then subsequent runs are silent.

# Single remote shell for test + wipe + make. The three operations
# are joined with && so an auth failure or any single command
# failing short-circuits the rest. MISSING_SIGNALK_DIR is the
# dedicated exit path for the config-not-plotter-is-running case.
Write-Host "Testing SSH + preparing remote folder (may prompt for password)..." -ForegroundColor DarkGray
$setupCmd = @(
    "test -d ~/.signalk || { echo 'MISSING_SIGNALK_DIR' >&2; exit 2; }",
    "rm -rf '$RemotePath'",
    "mkdir -p '$RemotePath'",
    "echo OK"
) -join " && "
$setupOut = ssh -o ConnectTimeout=10 $SshTarget $setupCmd 2>&1
if ($LASTEXITCODE -ne 0 -or $setupOut -notmatch "OK") {
    Write-Host $setupOut -ForegroundColor Yellow
    if ($setupOut -match "MISSING_SIGNALK_DIR") {
        throw "Remote ~/.signalk does not exist on $SshTarget."
    }
    throw "SSH setup to $SshTarget failed. Check: (1) you can 'ssh $SshTarget' manually, (2) the SSH user has rw access to ~/.signalk/node_modules, (3) ssh-copy-id $SshTarget for password-less deploys."
}

# Copy staging contents. scp prompts once more unless keys are set up.
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
Write-Host "  2. Open: http://home.ona:3000/$WebappName/"
Write-Host "     (or from the SignalK Webapps admin page)"
Write-Host ""
