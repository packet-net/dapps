#!/usr/bin/env python3
"""LinBPQ NET/ROM L4: a resent fragment is 20 bytes short. A self-contained reproduction.

    python3 l4frag_repro.py /path/to/linbpq

This script is LinBPQ's KISS-over-TCP TNC, the far end of the link
(a NET/ROM node, N0TST), and an AGW application on the LinBPQ node
(N0BPQ-6). Python 3 standard library only; no radio, no other software.

What it does:

1. N0TST sends NODES, brings up an L2 link, and opens a NET/ROM circuit
   to N0BPQ, then types the application command, so the circuit lands
   on the AGW application.
2. The application writes 256 bytes in one AGW 'D' frame. That is more
   than one NET/ROM frame carries (236), so LinBPQ sends it as two:
   236 bytes with More-follows, then 20.
3. N0TST acks both at L2 but never sends the L4 info ack, so after
   L4TIMEOUT (10 s here) LinBPQ resends both from its L4 hold queue.

Expected: the resent frames are the frames that were sent, 236 and 20
bytes. Actual, LinBPQ 6.0.26.1 and earlier: the resent first fragment
carries 216 bytes. The last 20 bytes of it are gone for good, and the
far end, which reassembles 216 + 20, gets a stream 20 bytes short with
nothing to say so.

Cause: SENDL4MESSAGE (L4Code.c) keeps a copy of each frame for retry
and, when it fragmented, sets the copy's LENGTH to MaxL4Len, the length
of the 236 data bytes with the buffer header but without the 20 bytes of
L3/L4 header. L3MSG->LENGTH, which the copy already has, is right:

    if (FRAGFLAG)
        Copy->LENGTH = MaxL4Len;    // should be MaxL4Len + 20, or not touched

Found by packet-net/dapps#425 (a DAPPS session stalls 180 s when it
happens: the receiver waits for 20 bytes that never come).
Exit status 0 on PASS, 1 on FAIL.
"""

from __future__ import annotations

import os
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

FEND, FESC, TFEND, TFESC = 0xC0, 0xDB, 0xDC, 0xDD
RR, SABM, UA, DISC, UI = 0x01, 0x2F, 0x63, 0x43, 0x03
PF = 0x10

CONFIG = """\
SIMPLE=1
NODECALL=N0BPQ
NODEALIAS=BPQ
LOCATOR=NONE
IDINTERVAL=0
BTINTERVAL=0
MINQUAL=1
L4TIMEOUT=10
L4RETRIES=3
AGWPORT={agw_port}
AGWSESSIONS=10
AGWMASK=255
APPLICATION 1,TESTAPP,,N0BPQ-6,BPQAPP,0

PORT
 ID=Link
 TYPE=ASYNC
 PROTOCOL=KISS
 IPADDR=127.0.0.1
 TCPPORT={kiss_port}
 CHANNEL=A
 SPEED=1200
 FRACK=7000
 RESPTIME=1000
 RETRIES=4
 MAXFRAME=4
 PACLEN=236
 QUALITY=200
 MINQUAL=1
ENDPORT
"""


def ax_call(call, last=False, bit7=False):
    c, _, s = call.partition("-")
    ssid = int(s or 0)
    return bytes(ord(x) << 1 for x in c.ljust(6)) + bytes([0x60 | (ssid << 1) | (0x80 if bit7 else 0) | (1 if last else 0)])


def nr_call(call):
    c, _, s = call.partition("-")
    return bytes(ord(x) << 1 for x in c.ljust(6)) + bytes([0x60 | (int(s or 0) << 1)])


def dec_call(b):
    c = "".join(chr(x >> 1) for x in b[:6]).strip()
    s = (b[6] >> 1) & 15
    return f"{c}-{s}" if s else c


ME, NODE = "N0TST", "N0BPQ"


def frame(ctl, info=b"", cmd=True, pid=None, to=NODE):
    return ax_call(to, bit7=cmd) + ax_call(ME, last=True, bit7=not cmd) + bytes([ctl]) + (bytes([pid]) if pid is not None else b"") + info


def kiss(body):
    out = bytearray([FEND, 0x00])
    for b in body:
        out += {FEND: bytes([FESC, TFEND]), FESC: bytes([FESC, TFESC])}.get(b, bytes([b]))
    out.append(FEND)
    return bytes(out)


class Far:
    """The far node: LinBPQ's KISS TNC and an L2 + NET/ROM peer."""

    def __init__(self, log):
        self.log = log
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", 0))
        self.srv.listen(1)
        self.port = self.srv.getsockname()[1]
        self.s = None
        self.vs = self.vr = 0
        self.l4 = []          # NET/ROM frames received: (opcode byte, l4 header, data)
        self.lock = threading.Lock()
        self.up = threading.Event()

    def accept(self, timeout):
        self.srv.settimeout(timeout)
        self.s, _ = self.srv.accept()
        threading.Thread(target=self._reader, daemon=True).start()

    def send(self, body):
        self.s.sendall(kiss(body))

    def _reader(self):
        buf, esc = bytearray(), False
        while True:
            try:
                d = self.s.recv(4096)
            except OSError:
                return
            if not d:
                return
            for b in d:
                if b == FEND:
                    if len(buf) > 15 and buf[0] == 0:
                        self._frame(bytes(buf[1:]))
                    buf = bytearray()
                elif b == FESC:
                    esc = True
                else:
                    if esc:
                        b = {TFEND: FEND, TFESC: FESC}.get(b, b)
                        esc = False
                    buf.append(b)

    def _frame(self, body):
        if dec_call(body[0:7]) != ME:
            return
        i = 14
        ctl = body[i]
        cmd = bool(body[6] & 0x80)
        with self.lock:
            if ctl & 1 == 0:
                ns, pf = (ctl >> 1) & 7, ctl & PF
                if ns == self.vr:
                    self.vr = (self.vr + 1) & 7
                    if body[i + 1] == 0xCF:
                        self._netrom(body[i + 2:])
                # ack every I frame at L2 (L2 is fine; only L4 is withheld)
                self.send(frame((self.vr << 5) | (PF if pf else 0) | RR, cmd=False))
            elif ctl & 3 == 1 and cmd and ctl & PF:
                self.send(frame((self.vr << 5) | PF | RR, cmd=False))
            elif ctl & ~PF & 0xFF == UA:
                self.up.set()

    def _netrom(self, p):
        org, dst = dec_call(p[0:7]), dec_call(p[7:14])
        hdr, data = p[15:20], p[20:]
        op = hdr[4]
        name = {1: "CONN REQ", 2: "CONN ACK", 3: "DISC REQ", 4: "DISC ACK", 5: "INFO", 6: "INFO ACK"}.get(op & 15, op & 15)
        more = " (more follows)" if op & 0x20 else ""
        extra = f" {len(data)} bytes{more}" if op & 15 == 5 else ""
        self.log(f"RX  NET/ROM {org}>{dst} {name} tx{hdr[2]} rx{hdr[3]}{extra}")
        self.l4.append((op, hdr, data))

    def send_netrom(self, l4):
        l3 = nr_call(ME) + nr_call(NODE) + bytes([7])
        with self.lock:
            self.send(frame((self.vr << 5) | (self.vs << 1), l3 + l4, pid=0xCF))
            self.vs = (self.vs + 1) & 7

    def wait(self, pred, timeout):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            with self.lock:
                for x in self.l4:
                    if pred(x):
                        self.l4.remove(x)
                        return x
            time.sleep(0.05)
        return None


class Agw:
    def __init__(self, port):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.s.settimeout(None)
        self.frames = []
        self.lock = threading.Lock()
        threading.Thread(target=self._reader, daemon=True).start()

    def send(self, kind, frm="", to="", data=b"", port=0, pid=0):
        h = struct.pack("<B3xcxBx10s10sI4x", port, kind.encode(), pid, frm.encode(), to.encode(), len(data))
        self.s.sendall(h + data)

    def _reader(self):
        buf = b""
        while True:
            try:
                d = self.s.recv(4096)
            except OSError:
                return
            if not d:
                return
            buf += d
            while len(buf) >= 36:
                n = struct.unpack("<I", buf[28:32])[0]
                if len(buf) < 36 + n:
                    break
                h, data, buf = buf[:36], buf[36:36 + n], buf[36 + n:]
                with self.lock:
                    self.frames.append((chr(h[4]), h[0], h[8:18].split(b"\0")[0].decode(), h[18:28].split(b"\0")[0].decode(), data))

    def wait(self, kind, timeout):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            with self.lock:
                for f in self.frames:
                    if f[0] == kind:
                        self.frames.remove(f)
                        return f
            time.sleep(0.05)
        return None


def run(linbpq):
    t0 = time.monotonic()
    lines = []

    def log(s):
        lines.append(f"{time.monotonic() - t0:6.2f}  {s}")
        print(lines[-1], flush=True)

    work = Path(tempfile.mkdtemp(prefix="l4frag-"))
    (work / "logs").mkdir()
    far = Far(log)
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    agw_port = probe.getsockname()[1]
    probe.close()
    (work / "bpq32.cfg").write_text(CONFIG.format(kiss_port=far.port, agw_port=agw_port))
    exe = os.path.abspath(linbpq if os.sep in linbpq else shutil.which(linbpq) or linbpq)
    out = open(work / "linbpq.out", "wb")
    proc = subprocess.Popen([exe], cwd=work, stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
    failed = None
    try:
        far.accept(30)
        log(f"LinBPQ connected to our KISS port; work dir {work}")
        time.sleep(1)
        agw = Agw(agw_port)
        agw.send("X", frm="N0BPQ-6")
        if not agw.wait("X", 5):
            raise RuntimeError("AGW register of N0BPQ-6 not answered")
        log("AGW application registered N0BPQ-6")

        # NODES from N0TST: we are a node, so LinBPQ can route back to us
        far.send(ax_call("NODES", bit7=True) + ax_call(ME, last=True) + bytes([UI, 0xCF, 0xFF]) + b"TST   ")
        time.sleep(1)
        far.send(frame(SABM | PF))
        if not far.up.wait(5):
            raise RuntimeError("no UA")
        log("L2 link up")
        time.sleep(1)

        # L4 connect: our circuit index 1, id 1, window 4, user N0USR
        far.send_netrom(bytes([1, 1, 0, 0, 1, 4]) + nr_call("N0USR") + nr_call(ME))
        ack = far.wait(lambda x: x[0] & 15 == 2, 10)
        if ack is None:
            raise RuntimeError("no CONN ACK")
        their = ack[1][2:4]   # their index and id
        far.rx = 0

        def iack(rx):
            far.send_netrom(their + bytes([0, rx, 6]))

        def info(tx, data):
            far.send_netrom(their + bytes([tx, far.rx, 5]) + data)

        log("--- circuit up; type the application command, so the circuit goes to the AGW app")
        info(0, b"TESTAPP\r")
        conn = agw.wait("C", 15)
        if conn is None:
            raise RuntimeError("the application never got the connect")
        remote = conn[2]
        log(f"AGW app: connected from {remote}")
        # take and ack what LinBPQ has said so far
        time.sleep(3)
        with far.lock:
            got = [x for x in far.l4 if x[0] & 15 == 5]
            far.l4.clear()
        far.rx = (max((x[1][2] for x in got), default=-1) + 1) & 0xFF
        iack(far.rx)
        time.sleep(1)

        payload = bytes(range(256))
        log("--- the app writes 256 bytes in one AGW frame; we ack at L2 but send no L4 info ack")
        agw.send("D", frm="N0BPQ-6", to=remote, data=payload, port=conn[1], pid=0xF0)
        first = []
        end = time.monotonic() + 5
        while len(first) < 2 and time.monotonic() < end:
            x = far.wait(lambda x: x[0] & 15 == 5, 1)
            if x:
                first.append(x)
        if len(first) < 2:
            raise RuntimeError(f"expected two INFO frames, got {len(first)}")
        log(f"first time: {[len(x[2]) for x in first]} bytes (tx {[x[1][2] for x in first]})")
        log("--- wait for LinBPQ's L4 timeout (L4TIMEOUT=10) and the resend")
        again = []
        end = time.monotonic() + 30
        while len(again) < 2 and time.monotonic() < end:
            x = far.wait(lambda x: x[0] & 15 == 5, 1)
            if x:
                again.append(x)
        if len(again) < 2:
            raise RuntimeError(f"expected the two INFO frames again, got {len(again)}")
        log(f"resent:     {[len(x[2]) for x in again]} bytes (tx {[x[1][2] for x in again]})")
        sent = b"".join(x[2] for x in first)
        resent = b"".join(x[2] for x in again)
        if resent != sent:
            missing = sorted(set(sent) - set(resent))
            failed = (f"the resend carries {len(resent)} bytes, the first send {len(sent)}: "
                      f"bytes {missing[0]}..{missing[-1]} of the app's write are gone" if missing else "the resend differs")
    except Exception as e:
        failed = f"could not run: {e!r}; see {work}"
    finally:
        proc.terminate()
        try:
            proc.wait(5)
        except subprocess.TimeoutExpired:
            proc.kill()
        out.close()
    if failed:
        log(f"*** {failed}")
    print(f"=== l4 fragment resend: {'FAIL' if failed else 'PASS'}")
    return 1 if failed else 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(run(sys.argv[1]))
