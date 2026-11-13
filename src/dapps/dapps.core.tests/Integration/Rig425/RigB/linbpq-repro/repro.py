#!/usr/bin/env python3
"""Minimal reproduction for packet-net/dapps#425: LinBPQ's AX.25 2.2 receiver
keeps out-of-window duplicate I frames and later delivers them as new data.

Plays the far end of an AX.25 link (N0TST) against a real LinBPQ (N0BPQ) over
KISS-over-TCP. LinBPQ connects to us as its TNC. Every frame is printed as it
goes, so the transcript is the evidence.

Usage: repro.py <scenario> [port]
  dup     duplicate I frames after an ack -> SREJ for an unsent frame, then
          stale frames delivered in the next numbering cycle (N(R) acks
          frames never sent)
  badnr   one N(R) that acks an unsent frame -> link silently ignores
          everything after it, then retries out (FRMRHACK)

Experiment patches against L2Code.c at the rig's pin (M0LTE/linbpq edcc798,
6.0.25.41), applied in order with patch -p1 and built with docker/Dockerfile:
  duplicate-guard.patch  drop an I frame whose N(S) is outside the receive
                         window (k = 4) instead of storing it and raising SREJ
  repeat-counter.patch   count only poll answers (RR/RNR/REJ F) towards the
                         "too many repeats of same I frame" disconnect
  single-poll.patch      no P bit on a batch's last I frame while an earlier
                         poll is still unanswered
Rig B runs either build with DAPPS_RIG_425_B_BPQ_IMAGE.
"""
import socket, sys, threading, time

FEND, FESC, TFEND, TFESC = 0xC0, 0xDB, 0xDC, 0xDD
ME, NODE = ("N0TST", 0), ("N0BPQ", 0)
t0 = time.monotonic()


def ts():
    return f"{time.monotonic() - t0:7.2f}"


def addr(call, ssid, bit7, last):
    b = bytes((ord(c) << 1) for c in call.ljust(6))
    return b + bytes([0x60 | (ssid << 1) | (0x80 if bit7 else 0) | (1 if last else 0)])


def frame(ctl, info=b"", cmd=True, pid=None):
    a = addr(*NODE, cmd, False) + addr(*ME, not cmd, True)
    body = a + bytes([ctl]) + (bytes([pid]) if pid is not None else b"") + info
    return body


def kiss(body):
    out = bytearray([FEND, 0x00])
    for b in body:
        out += {FEND: bytes([FESC, TFEND]), FESC: bytes([FESC, TFESC])}.get(b, bytes([b]))
    out.append(FEND)
    return bytes(out)


def describe(body):
    if len(body) < 15:
        return f"short {body.hex()}"
    def call(b):
        c = "".join(chr(x >> 1) for x in b[:6]).strip()
        s = (b[6] >> 1) & 15
        return c + (f"-{s}" if s else "")
    dst, src = call(body[0:7]), call(body[7:14])
    i = 14
    while not (body[i - 1] & 1):
        i += 7
    cmd = "C" if body[6] & 0x80 else "R"
    ctl = body[i]
    pf = (ctl >> 4) & 1
    rest = body[i + 1:]
    if ctl & 1 == 0:
        k = f"I {cmd}{' P' if pf else ''} S{(ctl >> 1) & 7} R{ctl >> 5} {rest[1:]!r}"
    elif ctl & 3 == 1:
        name = {0: "RR", 1: "RNR", 2: "REJ", 3: "SREJ"}[(ctl >> 2) & 3]
        k = f"{name} {cmd}{' P' if pf and cmd == 'C' else ' F' if pf else ''} R{ctl >> 5}"
    else:
        name = {0x2F: "SABM", 0x63: "UA", 0x43: "DISC", 0x0F: "DM", 0xAF: "XID", 0x87: "FRMR", 0x03: "UI", 0x6F: "SABME"}.get(ctl & 0xEF, f"U{ctl:02x}")
        k = f"{name} {cmd}{' P/F' if pf else ''} {rest.hex()}"
    return f"{src}>{dst} <{k}>"


class Link:
    def __init__(self, port):
        srv = socket.socket()
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("127.0.0.1", port))
        srv.listen(1)
        print(f"{ts()}  waiting for LinBPQ on KISS port {port}", flush=True)
        self.s, _ = srv.accept()
        print(f"{ts()}  LinBPQ connected", flush=True)
        self.rx = []
        self.lock = threading.Lock()
        threading.Thread(target=self.reader, daemon=True).start()

    def reader(self):
        buf, inframe, esc = bytearray(), False, False
        while True:
            d = self.s.recv(4096)
            if not d:
                return
            for b in d:
                if b == FEND:
                    if len(buf) > 1 and buf[0] == 0:
                        body = bytes(buf[1:])
                        with self.lock:
                            self.rx.append(body)
                        print(f"{ts()}  RX  {describe(body)}", flush=True)
                    buf = bytearray()
                elif b == FESC:
                    esc = True
                else:
                    if esc:
                        b = {TFEND: FEND, TFESC: FESC}.get(b, b)
                        esc = False
                    buf.append(b)

    def send(self, body, note=""):
        print(f"{ts()}  TX  {describe(body)}{'   # ' + note if note else ''}", flush=True)
        self.s.sendall(kiss(body))

    def wait_for(self, pred, timeout):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            with self.lock:
                for f in self.rx:
                    if pred(f):
                        self.rx.remove(f)
                        return f
            time.sleep(0.05)
        return None

    def drain(self):
        with self.lock:
            self.rx.clear()


def ctl_of(f):
    i = 14
    while not (f[i - 1] & 1):
        i += 7
    return f[i]


def I(ns, nr, data, p=False):
    return frame((nr << 5) | (0x10 if p else 0) | (ns << 1), data, cmd=True, pid=0xF0)


def RR(nr, cmd=True, pf=False):
    return frame((nr << 5) | (0x10 if pf else 0) | 0x01, cmd=cmd)


XID_INFO = bytes([0x82, 0x80, 0x00, 0x10, 0x02, 0x02, 0x00, 0x21, 0x03, 0x03, 0x22, 0xA4, 0x86, 0x06, 0x02, 0x08, 0x00, 0x08, 0x01, 0x07])


def connect(link):
    time.sleep(2)
    link.send(frame(0xBF, XID_INFO), "XID: offer AX.25 2.2 with SREJ, as LinBPQ itself does")
    assert link.wait_for(lambda f: ctl_of(f) & 0xEF == 0xAF, 5), "no XID response"
    link.send(frame(0x3F), "SABM")
    assert link.wait_for(lambda f: ctl_of(f) & 0xEF == 0x63, 5), "no UA"
    time.sleep(3)  # let any connect text arrive
    link.drain()


def scenario_dup(link):
    connect(link)
    print("--- 1. four new frames S0..S3, the last with P; LinBPQ should ack R4", flush=True)
    for ns, d in enumerate(b"abcd"):
        link.send(I(ns, 0, bytes([d]), p=(ns == 3)))
    time.sleep(3)
    print("--- 2. S2 and S3 again (P=0): retransmissions that crossed the ack. Already received,", flush=True)
    print("---    outside the receive window: the spec (2.3 draft 4.4.2, X.25 2.4.6.4(a)) says discard, no SREJ", flush=True)
    link.send(I(2, 0, b"c"), "duplicate")
    link.send(I(3, 0, b"d"), "duplicate")
    time.sleep(4)
    link.send(RR(0, cmd=True, pf=True), "poll: correct answer is RR F R4")
    time.sleep(4)
    print("--- 3. the next cycle: new frames S4..S7 then S0, S1 (P=0); we never send S2, S3 again", flush=True)
    for i, ns in enumerate([4, 5, 6, 7, 0, 1]):
        link.send(I(ns, 0, bytes([ord("e") + i])), "new")
    time.sleep(4)
    link.send(RR(0, cmd=True, pf=True), "poll: we have sent up to S1, so the only correct N(R) is 2")
    time.sleep(5)
    print("--- done", flush=True)


def scenario_badnr(link):
    connect(link)
    print("--- 1. ask the node for something so it sends us I frames", flush=True)
    link.send(I(0, 0, b"VERSION\r", p=False))
    f = link.wait_for(lambda f: ctl_of(f) & 1 == 0, 10)
    assert f, "LinBPQ sent no I frame"
    ns = (ctl_of(f) >> 1) & 7
    time.sleep(2)
    bad = (ns + 3) & 7
    link.send(RR(bad, cmd=False, pf=False), f"RR R{bad}: acks frames LinBPQ never sent (as a peer whose V(R) ran ahead would)")
    time.sleep(2)
    print("--- 2. from now on, valid frames: LinBPQ should answer them, or send FRMR", flush=True)
    link.send(I(1, (ns + 1) & 7, b"VERSION\r", p=True), "valid new I frame with poll")
    end = time.monotonic() + 60
    while time.monotonic() < end:
        f = link.wait_for(lambda f: True, 1)
        if f is None:
            continue
        c = ctl_of(f)
        if c & 0xEF == 0x43:
            link.send(frame(0x73, cmd=False), "UA to DISC")
            break
        if c & 3 == 1 and c & 0x10 and f[6] & 0x80:  # RR/RNR command with P
            link.send(RR(1, cmd=False, pf=True), "answer the poll, valid N(R)")
    time.sleep(2)
    print("--- done", flush=True)


if __name__ == "__main__":
    which = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 18101
    link = Link(port)
    {"dup": scenario_dup, "badnr": scenario_badnr}[which](link)
