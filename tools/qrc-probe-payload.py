"""Probe the structure of the GetPlayLyricInfo QRC payload.

The reference .qrc decoder (custom 3DES + zlib) reproduces byte-for-byte in our C#
port yet does not open this endpoint's payload, so the payload must differ in form.
This tries the plausible variations before giving up on the approach.
"""
import base64
import json
import ssl
import urllib.request
import zlib

ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE


def get(url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=30, context=ctx) as r:
        return r.read()


def fetch(crypt):
    payload = {
        "comm": {"ct": 24, "cv": 0},
        "req": {
            "module": "music.musichallSong.PlayLyricInfo",
            "method": "GetPlayLyricInfo",
            "param": {
                "songMID": "001zMQr71F1Qo8", "songID": 0,
                "format": "qrc", "qrc": 1, "trans": 0, "roma": 0,
                "crypt": crypt, "type": 1,
            },
        },
    }
    raw = get("https://u.y.qq.com/cgi-bin/musicu.fcg", data=json.dumps(payload).encode(),
              headers={"Referer": "https://y.qq.com/", "User-Agent": "Mozilla/5.0",
                       "Content-Type": "application/json"}).decode("utf-8")
    return json.loads(raw)["req"]["data"]


def entropy(b):
    from collections import Counter
    import math
    if not b:
        return 0.0
    c = Counter(b)
    return -sum((n / len(b)) * math.log2(n / len(b)) for n in c.values())


for crypt in (0, 1):
    print(f"########## crypt={crypt} ##########")
    data = fetch(crypt)
    print(f"  response crypt field = {data.get('crypt')!r}")
    b64 = data.get("lyric") or ""
    if not b64:
        print("  no lyric")
        print()
        continue

    raw = base64.b64decode(b64)
    print(f"  base64 len={len(b64)}  bytes={len(raw)}  mod8={len(raw) % 8}")
    print(f"  entropy = {entropy(raw):.2f} bits/byte  (8.0 = incompressible/encrypted)")
    print(f"  first 24 bytes: {' '.join(f'{x:02X}' for x in raw[:24])}")

    # zlib / raw deflate at several offsets: an unencrypted header would shift them.
    found = False
    for off in range(0, 25):
        for wbits, label in ((15, "zlib"), (-15, "raw")):
            try:
                out = zlib.decompress(raw[off:], wbits)
                if len(out) > 20:
                    print(f"  *** inflate OK at offset {off} as {label}: {len(out)} bytes")
                    print("      head:", out[:200].decode("utf-8", "replace"))
                    found = True
                    break
            except Exception:
                pass
        if found:
            break
    if not found:
        print("  no inflate offset/wrapper combination worked")
    print()
