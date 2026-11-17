# stage-only.ps1: copy of the staging steps from deploy.ps1 (no scp).
# Builds wwwroot is assumed to already exist at deploy/publish/wwwroot
# (run dotnet publish first or use deploy.ps1 -SkipBuild).
$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$WebappName = "signalk-onaplotter"
$WwwRootSrc = Join-Path $RepoRoot "deploy/publish/wwwroot"
$StagingDir = Join-Path $RepoRoot "deploy/staging"
$WebappStaging = Join-Path $StagingDir $WebappName
$PluginShim = Join-Path $PSScriptRoot "index.js"
$PackageJsonTemplate = Join-Path $PSScriptRoot "package.json.template"
$ProjectPath = Join-Path $RepoRoot "OnaPlotter/OnaPlotter.csproj"

if (-not (Test-Path $WwwRootSrc)) { throw "Need $WwwRootSrc; run dotnet publish first." }

# Patch index.html base href to subpath.
$IndexHtml = Join-Path $WwwRootSrc "index.html"
$content = Get-Content $IndexHtml -Raw
if ($content -notmatch "<base href=""/$WebappName/""") {
    $patched = $content -replace '<base href="/" />', "<base href=""/$WebappName/"" />"
    Set-Content -Path $IndexHtml -Value $patched -NoNewline
    Write-Host "Patched <base href> to /$WebappName/"
}

# Set ServerUrl to "auto" so the webapp talks to its host SK server.
$AppSettings = Join-Path $WwwRootSrc "appsettings.json"
Set-Content -Path $AppSettings -Value '{ "SignalK": { "ServerUrl": "auto" } }'

# The SDK precompressed index.html and appsettings.json before the
# patches above. SignalK server serves .br/.gz siblings of webapp files,
# so stale siblings would boot the app with base href "/".
foreach ($name in @("index.html", "appsettings.json")) {
    foreach ($ext in @(".br", ".gz")) {
        $sibling = Join-Path $WwwRootSrc "$name$ext"
        if (Test-Path $sibling) { Remove-Item -Force $sibling }
    }
}

# Stage.
if (Test-Path $StagingDir) { Remove-Item -Recurse -Force $StagingDir }
New-Item -ItemType Directory -Path $WebappStaging -Force | Out-Null

$csproj = Get-Content $ProjectPath -Raw
$versionMatch = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
$version = if ($versionMatch.Success) { $versionMatch.Groups[1].Value } else { (Get-Date -Format "1.0.yyyyMMdd.HHmm") }

$pkgJson = Get-Content $PackageJsonTemplate -Raw
$pkgJson = $pkgJson -replace '__VERSION__', $version
Set-Content -Path (Join-Path $WebappStaging "package.json") -Value $pkgJson
Copy-Item -Path $PluginShim -Destination (Join-Path $WebappStaging "index.js")
Copy-Item -Path (Join-Path $PSScriptRoot "README.npm.md") -Destination (Join-Path $WebappStaging "README.md")
Copy-Item -Path (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $WebappStaging "LICENSE")
Copy-Item -Recurse -Path $WwwRootSrc -Destination (Join-Path $WebappStaging "public")

Write-Host "Staged at $WebappStaging"
