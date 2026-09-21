# Update

How a node updates depends on how it was installed.

## apt installs

```bash
sudo apt update && sudo apt upgrade
```

That is the whole story. The package stops the old daemon, replaces the payload under `/usr/lib/dapps`, and restarts the service; `/etc/dapps/dapps.env` and the database in `/var/lib/dapps` are left alone.

The `.deb` deliberately ships **no** `dapps-updater` unit and seeds `DAPPS_UPDATE_CHECK_ENABLED=false`. Two reasons. The self-updater swaps a binary at `/opt/dapps/dapps`, which an apt install does not have and dpkg would not expect to change under it; and a banner announcing a release that `apt upgrade` is already bringing is a nag with nothing behind it - the dashboard's **Apply update** button has no updater to trigger. If you would rather see new upstream releases appear on the dashboard anyway, set `DAPPS_UPDATE_CHECK_ENABLED=true` in `/etc/dapps/dapps.env` and restart; the update itself still arrives through apt.

A release reaches the apt repository within about a minute of the tag: the release workflow tells [packet-net/apt](https://github.com/packet-net/apt) to reindex as its last step, and a nightly rebuild catches anything that dispatch missed.

The rest of this page is the **one-liner installer's** update story - the supervised in-place updater. It does not apply to apt installs.

## The mechanism

A separate, privileged systemd unit - `dapps-updater.service`, fired every 60 s by `dapps-updater.timer` - polls a marker file at `/var/lib/dapps/update-requested`. When the file is present, it runs `dapps --apply-update`, which:

1. Downloads the latest release for this platform from GitHub Releases.
2. Saves the current binary as `/opt/dapps/dapps.previous` (the rollback target).
3. Swaps the new binary into place.
4. Restarts `dapps.service`.
5. Verifies the new daemon stays up for 60 s.
6. **On any failure, restores `dapps.previous` and restarts again.**

The daemon itself has no permission to swap its own binary - that separation is what makes the update both safe (a compromised daemon can't replace itself silently) and supervised (the verification + rollback step happens whether you triggered it from the dashboard or via SSH).

This path is **Linux/systemd only** today. On Docker / Windows / macOS, the update banner still surfaces "v0.X.Y available" so you know when to do a manual binary-swap, but the one-click path doesn't apply.

## Path 1: dashboard banner + button

The simplest. The dashboard's update card shows current version + latest known release. If they differ, the **Apply update** button is enabled. Click it.

Behind the scenes, the dashboard writes the marker file, the next updater tick picks it up, and you watch the phase pill flip through `checking` → `downloading` → `swapping` → `restarting` → `verifying` → `success` (or `rolled back` / `failed`).

The dashboard polls the update phase live, so you don't need to refresh.

## Path 2: MCP tool - for AI assistants

If you're driving operations through an MCP-aware assistant, the `trigger_update` tool wraps the same marker-file write. Workflow:

```
1. check_for_updates       - force a re-poll of GitHub Releases
2. get_update_status       - confirm latest is what you expect
3. trigger_update          - write the marker
4. get_update_status       - poll until lastRun.phase == Success
                              and current == latest
```

Same code path as the dashboard button. The full deploy loop closes - an assistant that just merged a PR can verify the release exists and apply it on the destination node within a couple of minutes, with rollback handled automatically if the new build is broken.

## Path 3: command line

If you're already on the box:

```bash
sudo touch /var/lib/dapps/update-requested
```

Wait for the next updater tick (≤ 60 s). Or trigger immediately:

```bash
sudo systemctl start dapps-updater.service
```

Either way, the same supervised path runs.

## Rollback

The `--apply-update` path rolls back automatically on a failed verify. For an unprompted rollback (you applied an update, the daemon stayed up for 60 s and passed verify, but you've decided you don't want this version after all):

```bash
sudo /opt/dapps/dapps --rollback
```

This is the SSH-from-the-side-of-the-bed escape hatch: it restores `/opt/dapps/dapps.previous` unconditionally and restarts. Run it when the dashboard isn't reachable and you need the previous version back, fast.

## Channels and signing

Out of scope today.

- **Channels (stable / beta / dev).** The release stream is one channel. If we ever need staged rollouts the framework can carry channel pinning, but YAGNI until we have releases that warrant the distinction.
- **Binary signing.** Trust today is "GitHub Releases over HTTPS." Sigstore / minisign / etc. is its own initiative - separate decision when there's a real threat model that needs it.

## Auto-update on a schedule

Currently **parked**. Originally sketched as: opt-in `AutoUpdate=true` with a quiet-hours window, skipping if traffic was forwarded recently, with per-major-version pinning. Parked because the existing surfaces (banner + one-click + MCP tool) already cover today's operator population - author + a small handful of friendly nodes - and a scheduled-apply is a convenience for operators who don't watch the box at all, who don't yet exist. We'll revisit when there's a population that drifts weeks behind because nobody clicks the button.

## When updates check

The update checker polls GitHub Releases every hour (rate-limit-friendly: GitHub's unauthenticated limit is 60 req/hour per IP, so we use 1/60 of that). The poll's a single HTTPS request; the response feeds the dashboard banner, the heartbeat snapshot's `update_available` field, and the MCP `get_update_status` tool.

To force an immediate re-poll without waiting an hour:

- Dashboard: the **Check now** button on the update card.
- REST: `POST /Update/check`.
- MCP: `check_for_updates`.

## Disabling the update check

Set `DAPPS_UPDATE_CHECK_ENABLED=false` if your node has no internet access (or you really, really want to know about new versions some other way). The daemon will stop polling; the banner will go quiet. **Recommended only for offline deployments** - the cost is one HTTPS request per hour, the benefit is knowing about fixes. The apt package seeds it off for a different reason, covered at the top of this page.

## Dev builds

A dev build (anything not built from a tagged release commit) reports `isDevBuild: true` on the update endpoints. Dev builds **never auto-update**, even if the marker file is written, and the dashboard's Apply button is disabled. This is the developer escape hatch - you don't want a local hack getting silently overwritten by `latest` because the update timer fired at 03:00.
