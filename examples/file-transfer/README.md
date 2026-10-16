# File transfer (browser)

A small single-page app that picks a file in the browser, sends it over DAPPS to another callsign, and previews it on arrival - inline for `image/*` MIME types, as a download link otherwise.

Demonstrates:

- **MQTT-over-WebSocket from a browser**: the app speaks directly to DAPPS's `/mqtt` endpoint with `mqtt.js`. No REST round-trip per message.
- **Binary payload through DAPPS**: the file body is the MQTT payload, prefixed by a one-line JSON envelope giving the receiver the filename and MIME type.
- **DAPPS's transparent fragmentation**: F2 splits and reassembles automatically; the sender publishes one MQTT message and the receiver gets one delivery, regardless of the file's size relative to the operator's fragment threshold.

It does **not** demonstrate progressive / partial render, app-layer chunking, auth-required mode, or anything to do with TLS. Those are all worth doing - they're just not this example's job.

## Run it

You need a DAPPS instance reachable from your browser. The simplest setup is a single instance on `localhost`:

1. Start DAPPS the usual way ([Getting started](https://packet-net.github.io/dapps/getting-started/)). The dashboard's HTTP listener (default `:5000`) hosts both the dashboard and the `/mqtt` WebSocket endpoint this app talks to.
2. Open `index.html` directly from disk in a browser - double-click works on most desktop OSes, or `xdg-open` / `open` from the terminal.
3. Set the **dashboard URL** field at the top to your DAPPS base URL (e.g. `http://localhost:5000`). The app derives `ws://localhost:5000/mqtt` from it.
4. **Send panel**: pick a file, type the destination callsign, click **Send**.
5. **Receive panel**: click **Connect**. As messages addressed to `files@<this-node>` arrive, they appear with a preview or a download link.

The simplest demo is point-to-point on the same node: send to your own callsign and watch the file appear in the Receive panel of the same browser tab (or a second tab pointed at the same daemon).

## Two-node demo

For a real end-to-end demo, run two DAPPS instances and send between them. The mixed-bearer simulator (`scripts/sim-mixed-bearer.sh` in the repo) brings up a multi-node mesh; or do it manually with two daemons configured as neighbours of each other. Open `index.html` twice with each tab's dashboard URL pointing at one daemon.

## On-wire format

Each DAPPS message this app produces:

```
{"name":"<filename>","mime":"<mime-type>","size":<byte-count>}\n
<file bytes>
```

- One ASCII line of compact JSON.
- A single `\n` (0x0A) separator.
- The raw file bytes thereafter.

`name` is the sender's filename (used as the suggested download name). `mime` is the file's MIME type as the browser detected it. `size` is for sanity-checking on the receiver.

The sender publishes to `dapps/out/files/<destCallsign>` with user property `dapps-ttl=86400` (24 hours). The receiver subscribes to `dapps/in/files`, splits the payload at the first `\n`, parses the envelope, renders the result.

App slug: `files`. If you want a different slug, change the constant at the top of the JS in `index.html`.

## Auth-required mode

New installs require a token by default (`AuthRequired`), so each end usually needs an app token for the `files` slot:

1. Mint a token for app slug `files` on the dashboard's **App tokens** page (or see [Authentication](https://packet-net.github.io/dapps/app-developers/reference/#authentication) in the app-developers reference for the scripted recipe). Copy the plaintext token (only shown once).
2. In `index.html`'s connect form, paste the token into the **Token** field. The app uses it as the MQTT CONNECT password (with username `files`).

Each end needs its own token from its own DAPPS instance.

## Limits

DAPPS is **not** built for bulk transfer. The fragment threshold defaults to 4 KB; everything above that gets split, forwarded fragment-by-fragment, reassembled, and delivered. A 200 KB JPEG works fine, but moves slower than you'd want for anything interactive over a 1200-baud VHF link. A multi-MB file isn't what DAPPS is for - if you need that, set up a one-shot `scp` or HTTP fetch arranged via a DAPPS message.

## Offline / no-internet

The app loads `mqtt.js` from `unpkg.com` by default. To run offline, vendor the script:

```bash
curl -L https://unpkg.com/mqtt@5/dist/mqtt.min.js -o mqtt.min.js
```

Then change the `<script src="https://unpkg.com/mqtt@5/dist/mqtt.min.js">` in `index.html` to `<script src="mqtt.min.js">`.

## File listing

- `index.html` - the whole app. Inline CSS + JS for grokability.
- `sample-files/sample-image.jpg` - a small JPEG, for the "send something" smoke test.
- `sample-files/readme.txt` - a small text file, demonstrates the non-image download-link path.
