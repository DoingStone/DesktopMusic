"""Run the reference QRC decryption on live data.

Purpose: separate two hypotheses that the C# port cannot distinguish on its own —
either my port of the custom 3DES is wrong, or the key/format assumption is wrong.
If the reference implementation succeeds on the same bytes, the port is at fault.
"""
import base64
import json
import ssl
import urllib.request
import zlib

KEY = b"!@#)(*$%123ZXC!@!@#)(NHL"

# The reference module, fetched rather than vendored so it is exactly upstream.
REF_URL = ("https://raw.githubusercontent.com/L-1124/QQMusicApi/"
           "2290a32304bcd9052365f60c67f2d4f6b00e4d7e/qqmusic_api/algorithms/tripledes.py")

ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE


def get(url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=30, context=ctx) as r:
        return r.read()


print("downloading reference tripledes.py ...")
src = get(REF_URL).decode("utf-8")
ns = {}
exec(compile(src, "tripledes.py", "exec"), ns)
print(f"  loaded, has tripledes_crypt: {'tripledes_crypt' in ns}")

print()
print("fetching live QRC payload ...")
payload = {
    "comm": {"ct": 24, "cv": 0},
    "req": {
        "module": "music.musichallSong.PlayLyricInfo",
        "method": "GetPlayLyricInfo",
        "param": {
            "songMID": "001zMQr71F1Qo8", "songID": 0,
            "format": "qrc", "qrc": 1, "trans": 0, "roma": 0,
            "crypt": 0, "lrc_t": 0, "qrc_t": 0, "roma_t": 0, "trans_t": 0, "type": 1,
        },
    },
}
body = json.dumps(payload).encode("utf-8")
raw = get("https://u.y.qq.com/cgi-bin/musicu.fcg", data=body,
          headers={"Referer": "https://y.qq.com/", "User-Agent": "Mozilla/5.0",
                   "Content-Type": "application/json"}).decode("utf-8")
resp = json.loads(raw)
data = resp["req"]["data"]

print(f"  response crypt field = {data.get('crypt')!r}")
b64 = data.get("lyric") or ""
print(f"  lyric base64 length  = {len(b64)}")
if not b64:
    raise SystemExit("no lyric returned")

cipher = base64.b64decode(b64)
print(f"  cipher bytes         = {len(cipher)}  (mod 8 = {len(cipher) % 8})")

print()
print("=== attempt 1: plain zlib, no decryption ===")
try:
    out = zlib.decompress(cipher)
    print(f"  SUCCESS: {len(out)} bytes")
    print("  head:", out[:200].decode("utf-8", "replace"))
except Exception as e:
    print(f"  failed: {type(e).__name__}: {e}")

print()
print("=== attempt 2: reference 3DES then zlib ===")
schedule = ns["tripledes_key_setup"](KEY, ns["DECRYPT"])
plain = bytearray()
for i in range(0, len(cipher), 8):
    plain += ns["tripledes_crypt"](cipher[i:i + 8], schedule)

print(f"  decrypted {len(plain)} bytes, first 8 = "
      f"{' '.join(f'{b:02X}' for b in plain[:8])}")
try:
    out = zlib.decompress(bytes(plain))
    print(f"  SUCCESS: {len(out)} bytes")
    print("  head:", out[:300].decode("utf-8", "replace"))
except Exception as e:
    print(f"  inflate failed: {type(e).__name__}: {e}")
    # Raw deflate is the other possibility.
    try:
        out = zlib.decompress(bytes(plain), -15)
        print(f"  SUCCESS as raw deflate: {len(out)} bytes")
        print("  head:", out[:300].decode("utf-8", "replace"))
    except Exception as e2:
        print(f"  raw deflate also failed: {type(e2).__name__}: {e2}")
