# Install on macOS

macOS runs DAPPS the same way Linux does - drop a binary on disk, run it. There's no signed installer or `launchd` integration in the release; if you want it to start on boot you wire that up yourself with a `launchd` plist.

The release binary targets **Apple Silicon (arm64)** only. If you're on an Intel mac, build from source.

## 1. Drop the binary

```bash
sudo mkdir -p /opt/dapps /var/lib/dapps
sudo curl -L \
  https://github.com/packet-net/dapps/releases/latest/download/dapps-osx-arm64 \
  -o /opt/dapps/dapps
sudo chmod +x /opt/dapps/dapps
```

The binary is **not notarised** - Gatekeeper will refuse to run it on first launch with "cannot be opened because the developer cannot be verified." Either:

- Strip the quarantine attribute: `sudo xattr -d com.apple.quarantine /opt/dapps/dapps`, or
- Right-click → Open in Finder once to add a one-time exception.

## 2. Run interactively first

```bash
sudo -u $USER \
  DAPPS_CALLSIGN=M0LTE-1 \
  DAPPS_NODE_HOST=127.0.0.1 \
  DAPPS_AGW_PORT=8000 \
  /opt/dapps/dapps
```

For XRouter (RHPv2 instead of AGW), substitute `DAPPS_NODE_BEARER=rhpv2 DAPPS_RHP_PORT=9000` for `DAPPS_AGW_PORT=8000`. See [Connect via XRouter](../connect/xrouter.md).

Open `http://localhost:5000/` in a browser, set an admin password on `/Setup`. `Ctrl+C` to stop.

## 3. Run on boot via launchd (optional)

Save to `/Library/LaunchDaemons/uk.m0lte.dapps.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>uk.m0lte.dapps</string>
  <key>ProgramArguments</key>
  <array>
    <string>/opt/dapps/dapps</string>
  </array>
  <key>WorkingDirectory</key>
  <string>/var/lib/dapps</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>DAPPS_CALLSIGN</key><string>M0LTE-1</string>
    <key>DAPPS_NODE_HOST</key><string>127.0.0.1</string>
    <key>DAPPS_AGW_PORT</key><string>8000</string>
    <key>ASPNETCORE_URLS</key><string>http://0.0.0.0:5000</string>
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>/var/log/dapps.log</string>
  <key>StandardErrorPath</key><string>/var/log/dapps.log</string>
</dict>
</plist>
```

Load and start:

```bash
sudo launchctl bootstrap system /Library/LaunchDaemons/uk.m0lte.dapps.plist
sudo launchctl enable system/uk.m0lte.dapps
```

Stop / unload:

```bash
sudo launchctl bootout system/uk.m0lte.dapps
```

To upgrade, stop launchd, swap in the new binary from the [release page](https://github.com/packet-net/dapps/releases/latest), and start it again. DAPPS does not update itself; the dashboard's banner tells you when a new version is out. See [Upgrade](../update.md).

## Logs

If running interactively, stdout. Under launchd, the file you set as `StandardOutPath` (above), or `log show --predicate 'process == "dapps"'` for the system log.
