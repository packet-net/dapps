# Letters - long-form messenger (browser)

A single-page browser app for **long-form correspondence** over DAPPS. Like email, not chat - subject lines, multi-paragraph bodies, threaded conversations per peer, persistent across reloads.

Companion to [`file-transfer/`](../file-transfer/): same MQTT-over-WebSocket transport pattern, different shape (text + threads + IndexedDB persistence rather than one-shot binary).

## What it demonstrates

- **MQTT-over-WebSocket from a browser** to DAPPS's `/mqtt` endpoint - same pattern as `file-transfer/`.
- **IndexedDB persistence**. Conversations + read/unread + first-run state survive reloads, so you can close the tab and come back to find your history intact.
- **Long-form composition**. Subject + multi-paragraph body. The wire envelope explicitly carries the sender's composition timestamp so messages within a thread sort sensibly even when DAPPS's at-least-once delivery delivers them out of order.
- **App-defined envelope on top of a DAPPS payload**. The example shows the pattern: one ASCII line of compact JSON for metadata, then the body. Same shape as `file-transfer/` minus the `\n` separator (since the body here is already inside the JSON).
- **Self-loopback handling**. When you send a letter to your own callsign, the broker delivers it back to your subscribe. The app filters those out by matching `dapps-source` against the operator's own callsign.

## What it doesn't

- Read receipts. DAPPS doesn't track read state - that's an app-layer concern. Adding "read" notifications would mean a reverse DAPPS message per inbound; would also work, just isn't here.
- Attachments. `file-transfer/` covers binary file shipment as a separate pattern; combining the two is a future exercise.
- Markdown / rich text rendering. Body is plain text; newlines are paragraph breaks.
- Multiple threads per peer (subject-based threading). v1 is per-peer-only; if you want to discuss two unrelated topics with the same person they're in one timeline.
- Group conversations / many-to-many fanout.

## Run it

You need a DAPPS instance reachable from your browser. The simplest setup is a single instance on `localhost`:

1. Start DAPPS the usual way ([Getting started](https://packet-net.github.io/dapps/getting-started/)). Set the callsign (e.g. `M0LTE-1`).
2. Open `index.html` directly from disk in a browser - double-click works on most desktop OSes, or `xdg-open` / `open` from the terminal.
3. **First run only**: a modal asks for "Your callsign". Type the same callsign your DAPPS instance uses. Persisted in IndexedDB; you can change it later via the "change" link in the header.
4. Click **Connect**. Default dashboard URL is `http://localhost:5000`; the app derives `ws://localhost:5000/mqtt` from it.
5. Click **New letter**, type a destination callsign, a subject, a body, click **Send**.
6. The letter appears in the thread immediately (rendered optimistically from the local store).

The simplest demo is point-to-point on the same node: send to your own callsign and the receive subscription will pick it up. The app filters those self-loopback echoes out so you don't see the message twice.

For a real two-node demo, run two DAPPS instances configured as neighbours (or use `scripts/sim-multihop.sh` from the repo for a small mesh). Open `index.html` in two browsers, each pointed at one daemon. Send across.

## On-wire envelope

Each DAPPS message this app produces has a single JSON object as its payload:

```json
{
  "v": 1,
  "subject": "Re: HF tonight",
  "body": "Heard you on 14.230. Conditions were great…\n\n73",
  "ts": "2026-05-04T19:30:00Z"
}
```

| Field   | Type   | Required | Notes                                                                   |
|---------|--------|----------|-------------------------------------------------------------------------|
| `v`     | int    | yes      | Envelope version. `1` today; receivers ignore unknown versions.         |
| `subject` | string | yes (may be empty) | Optional subject line. Empty string allowed; renders as "(no subject)" in the UI. |
| `body`  | string | yes      | Plain-text body. Newlines separate paragraphs.                          |
| `ts`    | string | yes      | ISO-8601 sender-side composition timestamp. Used for in-thread ordering since DAPPS's delivery order isn't guaranteed for messages spaced apart in time. |

The MQTT publish goes to `dapps/out/letters/<destCallsign>` with user property `dapps-ttl=604800` (7 days - long-form correspondence is "still useful next week").

The receive subscription is on `dapps/in/letters`. The app reads `dapps-id` and `dapps-source` user properties to populate the per-letter row in IndexedDB.

App slug: `letters`.

## Auth-required mode

New installs require a token by default (`AuthRequired`); mint one for app slug `letters` on the dashboard's **App tokens** page (or see [Authentication](https://packet-net.github.io/dapps/app-developers/reference/#authentication) in the app-developers reference for the scripted recipe), then paste the plaintext token into the **Token** field of the Connect modal. The app uses it as the MQTT CONNECT password (with username `letters`).

Each end needs its own token from its own DAPPS instance.

## Storage notes

The app uses a single IndexedDB database called `dapps-letters`:

- **`letters`** object store - one row per letter, keyed on the DAPPS message id (`dapps-id`) for inbound, or a local UUID for outbound (since the daemon-assigned id isn't observable from the publish API). Indexed by `peer` for fast per-conversation queries.
- **`meta`** object store - small KV map: `myCallsign`, `dashboardUrl`, `token`.

To reset the app, open browser dev tools and delete the `dapps-letters` IndexedDB. The chat history and your stored callsign / dashboard URL all live there.

## Limits

- DAPPS isn't built for bulk transfer. A 4-page letter is fine. A 40-page essay works but takes minutes to ship over a 1200-baud VHF link, and stresses the F2 fragmentation buffers. The composer warns when your body crosses the operator's fragment threshold (default 4 KB).
- No search. Conversations are sorted by recency; messages within a conversation are chronological. If you need to find an old letter, scroll, or use browser-native find-on-page within the thread.
- No undo on send. Submitted letters are queued in DAPPS's outbound database immediately and can't be cancelled from the app.

## Offline / no-internet

Same as `file-transfer/` - the app loads `mqtt.js` from `unpkg.com`. To run offline, vendor the script:

```bash
curl -L https://unpkg.com/mqtt@5/dist/mqtt.min.js -o mqtt.min.js
```

Then change the `<script src>` in `index.html` to `mqtt.min.js`.

## File listing

- `index.html` - the whole app. Inline CSS + JS for grokability.
- `README.md` - this file.
