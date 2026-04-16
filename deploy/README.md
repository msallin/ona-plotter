# Deploy

Builds OnaPlotter as a SignalK webapp and deploys it to a SignalK server via SSH.

## Prerequisites

- .NET 10 SDK with `wasm-tools` workload
- `ssh` and `scp` in PATH (Windows 10+ has these built in via OpenSSH client)
- SSH key set up for the target host (otherwise you'll type your password twice)
- Write access to `~/.signalk/node_modules/` on the target

## Run

From the repo root:

```powershell
pwsh ./deploy/deploy.ps1
```

Default target is `pi@openplotter.local`. Override with `-SshTarget`:

```powershell
pwsh ./deploy/deploy.ps1 -SshTarget "pi@10.0.0.123"
pwsh ./deploy/deploy.ps1 -SshTarget "pi@openplotter.tail66b58.ts.net"
```

## What it does

1. `dotnet publish -c Release` into `deploy/publish/`.
2. Patches `index.html` `<base href>` to `/signalk-onaplotter/` so Blazor routing works under the SignalK subpath.
3. Creates a SignalK webapp bundle (`package.json` + `public/`) in `deploy/staging/signalk-onaplotter/`.
4. Wipes the old install and `scp`s the bundle to `~/.signalk/node_modules/signalk-onaplotter/`.

## After deploy

Restart the SignalK server so it picks up the new webapp:

```bash
ssh pi@openplotter.local 'sudo systemctl restart signalk'
```

Or restart via the OpenPlotter UI. Then open:

```
http://openplotter.local:3000/signalk-onaplotter/
```

The webapp also shows up in the SignalK server admin UI under *Webapps*.

## Skip rebuild

To redeploy without rebuilding (e.g., after just tweaking `package.json`):

```powershell
pwsh ./deploy/deploy.ps1 -SkipBuild
```
