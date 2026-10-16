# Concepts

Read this once before writing your first DAPPS app. It's the mental model - what DAPPS *is*, what it deliberately *isn't*, and what those choices imply.

## The queue addressed by `app@CALLSIGN`

Every DAPPS message has a destination of the form `app@CALLSIGN` - for example `chat@G7XYZ` or `weather@M0LTE-1`.

The **callsign** identifies the *node* the message is for. It's the same callsign the operator has registered with their packet node, and it's how the radio network already routes traffic. SSID matters: `M0LTE-1` is a different destination from `M0LTE-7`.

The **app slug** identifies which queue on that node. It's a free-form ASCII label chosen by the developer - `chat`, `weather`, `apt-update`, whatever. Apps subscribe to their own slot (`dapps/in/<app>`) and ignore everything else.

This means **one node can host many apps simultaneously**, each with its own private queue. It also means **the same app can run on many nodes** - `chat@G7XYZ` and `chat@M0LTE-1` are different conversations between different people.

There is no central registry of app slugs. If you pick `weather` and someone else also picks `weather`, you're both running an app called `weather` and you'll talk only to the corresponding `weather` instance on whichever node a message is addressed to. Pick descriptive slugs if you want to interoperate with other apps; pick whatever you like if you don't.

## At-least-once + content-addressed = idempotent

DAPPS guarantees **at-least-once** delivery. If the local DAPPS crashes after handing a message to your app but before recording the ack, your app will see the same message again on next subscribe.

The way out is to lean on the message id. Every DAPPS message has a `dapps-id` (the first seven hex characters of `sha1(salt ++ payload)`), so a redelivered message arrives with **the same id**. An app that records "ids I've already processed" handles duplicates correctly with no further coordination - no monotonic sequence numbers, no per-sender state machine, no transactions across the DAPPS boundary.

In practice this is a one-line check at the top of your message handler:

```python
if message_id in already_processed:
    ack(message_id)
    return
already_processed.add(message_id)
# ... do the actual work ...
ack(message_id)
```

The set can be a SQLite table, a Redis key with a TTL, a file, whatever fits the app. The point is that DAPPS doesn't impose a storage decision on you; it just gives you a stable id to key off.

## TTL: how long is this still useful?

When an app submits a message, it can include a **TTL** in seconds - "this is still worth delivering for the next N seconds; after that, drop it." The TTL travels on the wire and is decremented at every hop by the time the message spent queued on that hop. A message with TTL 300 that sat in two intermediate queues for 60 s each arrives at the destination with about 180 s left.

On delivery, DAPPS surfaces the **residual** TTL to the receiving app - both REST and MQTT report the same number. An app reading `ttl=2` knows the message is nearly stale; an app reading `ttl=null` knows the sender didn't bother to specify one (in which case the message never expires: it waits until it is forwarded, or acked by the app it is for, and the node deletes it 30 days after that).

Pick a TTL that matches the app:

- **Chat / presence**: short (60–300 s). Stale chat is annoying.
- **Bulletins / news**: medium (hours).
- **Configuration updates**: long (a day or more) - the receiver wants the latest, but yesterday's update is still useful if today's hasn't arrived yet.
- **No TTL at all**: only if the message will *always* be useful no matter how late it arrives.

## Source tracking

Every DAPPS message carries a `dapps-source` user property - the originating callsign. This is set by DAPPS at the source node, propagated end-to-end with no app intervention, and surfaced to the receiving app so a reply can be addressed correctly.

For an interactive app (chat, request/response), the typical pattern is: receive a message, look at `dapps-source`, decide what to send back, submit a new message addressed to `<reply-app>@<source>`. The two messages are independent - no built-in correlation.

## What DAPPS is not

- **Not real-time.** Latency from "submit" to "destination app sees it" can be seconds, minutes, hours, or days. If you need an answer back inside a TCP timeout, DAPPS is the wrong tool.
- **Not RPC.** There's no built-in "call and wait for reply." If your app wants a response, it submits a separate message and the other side replies the same way. The two messages are independent - a reply might arrive before the original sender's app has even reconnected to read its own outbox-confirmation.
- **Not packet mail.** Packet mail (BBS, Winlink) is built around humans: subjects, bodies, threads. A program reading from a queue named `weather` wants none of that. It wants `byte[] payload` arriving on a socket. DAPPS uses the *peering and routing patterns* mail has worked out without inheriting the mail-shaped surface area.
- **Not transport-coupled.** DAPPS speaks a small text protocol that rides on top of whatever bearer is available - AGW today, MeshCore or RHPv2 tomorrow. An app sees the same queue regardless.

## Why DAPPS, not raw AX.25?

Raw AX.25 connections are point-to-point, session-oriented, and ephemeral. If you write your app directly against AX.25, you end up writing - *every time* - the same ten things: connection retries, partial-write handling, "what if the other end isn't listening right now," "what if the link drops mid-message," idempotent reprocessing on reconnect, framing for application-level messages, routing across multiple bearers, and so on.

DAPPS does that work once, in one place, so the app gets to be just an app:

```python
on_message("hello") → reply("world")
```

vs.

```python
loop:
    try connect to G7XYZ
    if connected: send "hello"
    else: schedule retry, persist to disk, hope BPQ is up
    on partial response: figure out how much arrived, reconnect, ask again
    on duplicate: was that the original or a redelivery?
```

## Multi-part messages, transparently

Payloads larger than the operator's configured fragment threshold (default 4 KB) are split into N fragments at submit, each delivered independently and reassembled at the receiver. From the app's perspective this is invisible - you submit a 50 KB message, the destination app receives a 50 KB message, and the sender doesn't see N separate "delivered" notifications.

What you should know:

- **Per-hop reassembly**, not end-to-end reassembly. Intermediate nodes buffer fragments and forward the reassembled message - they don't pass fragments along independently.
- **Reassembly buffers age out** after the operator's configured timeout (7 days by default). A message whose fragments take longer than that to all arrive will fail.
- **Fragments are independently retried**, so a partial multi-part message doesn't restart from fragment 0 if the link drops at fragment 7.

For most apps, treat multi-part as "transparent - your message goes." For very large messages, consider whether DAPPS is the right layer at all (it isn't for MB-scale data).

## Binary payloads

DAPPS doesn't impose any encoding on the payload. The bytes you submit are the bytes the destination app receives. JSON, Protocol Buffers, MessagePack, an image file, a TLS handshake - DAPPS doesn't care. Both interfaces give you a byte array:

- **MQTT**: the publish payload is `byte[]`. Use it directly.
- **REST**: the JSON request/response wraps the bytes as a base64 string in the `payload` field. The example apps decode it on receive and base64-encode on submit.

A worked browser example is [`examples/file-transfer/`](https://github.com/packet-net/dapps/tree/master/examples/file-transfer) - picks a file, sends it, previews it on arrival inline if it's `image/*` or as a download link otherwise. The app puts a one-line JSON envelope in front of the file body so the receiver knows the filename and MIME type; that envelope is the **app's** convention, not DAPPS's. A sibling [`examples/letters/`](https://github.com/packet-net/dapps/tree/master/examples/letters) shows the same envelope-on-top-of-bytes pattern for long-form text correspondence (subject + multi-paragraph body) with IndexedDB persistence for the conversation timeline.

For payloads above the fragment threshold, the multi-part machinery above kicks in - again invisibly. There's no "the sender chunked it deliberately" path that the receiver needs to be aware of: app code submits one whole message, app code receives one whole message, DAPPS handles the wire-level splitting in between.

## Where to next

- Walk [a working hello-world app](tutorial.md) in Python, end-to-end.
- Read the [reference](reference.md) for every endpoint and topic.
- Browse the [sample gallery](gallery.md) for runnable examples.
