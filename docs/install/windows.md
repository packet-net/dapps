# Install on Windows

Windows runs DAPPS the same way Linux does - drop a binary on disk, run it. There's no signed installer or service-controller integration today; if you want it as a service you wire that up yourself with `nssm` or the built-in `sc.exe`.

## 1. Drop the binary

Download `dapps-win-x64.exe` from the [latest release](https://github.com/packet-net/dapps/releases/latest) and put it somewhere sensible - `C:\Program Files\dapps\dapps.exe` is conventional.

## 2. Run interactively first

A console run is the quickest way to verify everything's wired up before you bother making it a service:

```powershell
$env:DAPPS_CALLSIGN = "M0LTE-1"
$env:DAPPS_NODE_HOST = "127.0.0.1"
$env:DAPPS_AGW_PORT = "8000"
& "C:\Program Files\dapps\dapps.exe"
```

For XRouter (RHPv2 instead of AGW), set `$env:DAPPS_NODE_BEARER = "rhpv2"` and `$env:DAPPS_RHP_PORT = "9000"` instead of `DAPPS_AGW_PORT`. See [Connect via XRouter](../connect/xrouter.md).

You should see `Now listening on: http://localhost:5000` and the rest of the start-up lines. Open `http://localhost:5000/` in a browser, set an admin password on `/Setup`, and you're in.

`Ctrl+C` to stop.

## 3. Run as a Windows service (optional)

The simplest is [NSSM (the Non-Sucking Service Manager)](https://nssm.cc/). After installing it:

```powershell
nssm install DAPPS "C:\Program Files\dapps\dapps.exe"
nssm set DAPPS AppDirectory "C:\Program Files\dapps"
nssm set DAPPS AppEnvironmentExtra `
  DAPPS_CALLSIGN=M0LTE-1 `
  DAPPS_NODE_HOST=127.0.0.1 `
  DAPPS_AGW_PORT=8000
nssm start DAPPS
```

NSSM handles restart-on-crash and stdout-to-event-log capture without you writing wrappers.

The supervised in-place update path (the `/Update/apply` button on the dashboard) is **Linux only** today - it relies on systemd. On Windows you replace the binary by stopping the service, swapping the file, and starting again. The dashboard's update banner still surfaces "v0.X.Y available" so you know when to do that.

## State location

By default DAPPS writes the SQLite database to `dapps.db` in its working directory (or `data\dapps.db` if there's a `data` folder there). If you ran from `C:\Program Files\dapps` the OS may not let it write there - set the working directory to a writable location (e.g. `C:\ProgramData\dapps`).

```powershell
nssm set DAPPS AppDirectory "C:\ProgramData\dapps"
```

## Firewall

Windows Firewall will prompt the first time DAPPS binds the dashboard / MQTT ports. Allow it for the network profile that matches your radio setup. Out of the box the dashboard only listens on localhost. If you want it LAN-reachable, set `ASPNETCORE_URLS=http://0.0.0.0:5000` in the environment and allow the rule on Private.

## Logs

When run from a console, DAPPS logs to stdout. When run as a service via NSSM, point NSSM's stdout/stderr capture at a file:

```powershell
nssm set DAPPS AppStdout "C:\ProgramData\dapps\dapps.log"
nssm set DAPPS AppStderr "C:\ProgramData\dapps\dapps.log"
```

NSSM rotates by size; consult its docs for the exact knobs.
