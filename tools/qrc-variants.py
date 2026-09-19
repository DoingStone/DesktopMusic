"""Last two variants for the GetPlayLyricInfo payload.

The .qrc scheme (custom 3DES in ECB + zlib) is ruled out: our port reproduces it
byte-for-byte and the reference itself fails on this payload. Two plausible variants
remain and are cheap to test - CBC instead of ECB, and a leading IV block.
"""
import base64
import json
import ssl
import urllib.request
import zlib

ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
KEY = b"!@#)(*$%123ZXC!@!@#)(NHL"


def get(url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=30, context=ctx) as r:
        return r.read()


ns = {}
src = get("https://raw.githubusercontent.com/L-1124/QQMusicApi/"
          "2290a32304bcd9052365f60c67f2d4f6b00e4d7e/qqmusic_api/algorithms/tripledes.py").decode()
exec(compile(src, "tripledes.py", "exec"), ns)

payload = {
    "comm": {"ct": 24, "cv": 0},
    "req": {"module": "music.musichallSong.PlayLyricInfo", "method": "GetPlayLyricInfo",
            "param": {"songMID": "001zMQr71F1Qo8", "songID": 0, "format": "qrc",
                      "qrc": 1, "trans": 0, "roma": 0, "crypt": 0, "type": 1}},
}
data = json.loads(get("https://u.y.qq.com/cgi-bin/musicu.fcg",
                      data=json.dumps(payload).encode(),
                      headers={"Referer": "https://y.qq.com/", "User-Agent": "Mozilla/5.0",
                               "Content-Type": "application/json"}).decode())["req"]["data"]
cipher = base64.b64decode(data["lyric"])

enc = ns["tripledes_key_setup"](KEY, ns["ENCRYPT"])
dec = ns["tripledes_key_setup"](KEY, ns["DECRYPT"])


def xor(a, b):
    return bytes(x ^ y for x, y in zip(a, b))


def try_inflate(b, label):
    for wbits, name in ((15, "zlib"), (-15, "raw")):
        try:
            out = zlib.decompress(b, wbits)
            if len(out) > 20:
                print(f"  *** {label} + {name}: {len(out)} bytes")
                print("      head:", out[:250].decode("utf-8", "replace"))
                return True
        except Exception:
            pass
    return False


print("=== variant A: 3DES-CBC, zero IV ===")
# CBC decrypt: P = D(C) xor prev. Encryption used the custom ENCRYPT schedule for the
# forward direction, so decryption uses the DECRYPT schedule.
prev = bytes(8)
out = bytearray()
for i in range(0, len(cipher), 8):
    block = cipher[i:i + 8]
    out += xor(ns["tripledes_crypt"](block, dec), prev)
    prev = block
if not try_inflate(bytes(out), "CBC/zero-IV"):
    print(f"  no. first 8 = {' '.join(f'{b:02X}' for b in out[:8])}")

print()
print("=== variant B: ECB, first block treated as IV (skip it) ===")
out2 = bytearray()
for i in range(8, len(cipher), 8):
    out2 += ns["tripledes_crypt"](cipher[i:i + 8], dec)
if not try_inflate(bytes(out2), "ECB/skip-first"):
    print(f"  no. first 8 = {' '.join(f'{b:02X}' for b in out2[:8])}")

print()
print("=== variant C: ECB over the whole payload, then raw deflate at any offset ===")
out3 = bytearray()
for i in range(0, len(cipher), 8):
    out3 += ns["tripledes_crypt"](cipher[i:i + 8], dec)
hit = False
for off in range(0, 33):
    for wbits in (15, -15):
        try:
            r = zlib.decompress(bytes(out3[off:]), wbits)
            if len(r) > 20:
                print(f"  *** ECB + inflate at offset {off}: {len(r)} bytes")
                print("      head:", r[:250].decode("utf-8", "replace"))
                hit = True
                break
        except Exception:
            pass
    if hit:
        break
if not hit:
    print("  no")

print()
print("=== variant D: inflate the ciphertext, then 3DES the result ===")
try:
    mid = zlib.decompress(cipher)
    print(f"  inflated directly: {len(mid)} bytes")
except Exception as e:
    print(f"  not plain zlib: {e}")
