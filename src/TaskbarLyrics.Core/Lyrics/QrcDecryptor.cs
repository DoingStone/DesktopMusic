using System.IO.Compression;
using System.Text;

namespace TaskbarLyrics.Core.Lyrics;

/// <summary>
/// Decrypts QQ Music QRC lyrics.
/// <para>
/// QRC is the only source that carries <b>per-word</b> timing, which is what lets the
/// karaoke highlight follow a singer who rushes or drags. Without it the highlight can
/// only be interpolated across a line from its two endpoints, and no interpolation can
/// recover pacing that was never transmitted.
/// </para>
/// <para>
/// The payload is Triple-DES in ECB mode over 8-byte blocks, then zlib. The DES here is
/// <b>not</b> standard DES: QQ's variant carries a deliberate deviation in the PC-2 key
/// schedule (the second half of the compression table is offset by 27 rather than the
/// usual amount). That is why <see cref="System.Security.Cryptography.TripleDES"/> cannot
/// be used — it produces garbage for the same key. The schedule below is a faithful port
/// of the reference implementations.
/// </para>
/// </summary>
public static class QrcDecryptor
{
    /// <summary>QQ's fixed 24-byte Triple-DES key.</summary>
    private static readonly byte[] QqKey = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    private const int Decrypt = 0;
    private const int Encrypt = 1;

    /// <summary>
    /// Why the last <see cref="TryDecrypt"/> returned null. Diagnostics only; it exists
    /// because a silent fallback to LRC gives no clue which stage failed.
    /// </summary>
    public static string LastDiagnostic { get; private set; } = "not attempted";

    /// <summary>
    /// Read a QRC payload in whichever form the server sent it.
    /// <para>
    /// The response's own <c>crypt</c> field says whether encryption was applied. When
    /// it is 0 the payload is only compressed, and running 3DES over it first produces
    /// noise. Compressed data looks like random bytes either way, so the only reliable
    /// discriminator is to try the cheap path first.
    /// </para>
    /// </summary>
    public static string? TryRead(byte[] payload)
    {
        var direct = TryInflate(payload);
        if (direct is not null)
        {
            LastDiagnostic = $"plain zlib, {direct.Length} chars";
            return direct;
        }

        return TryDecrypt(payload);
    }

    /// <summary>
    /// Inflate a compressed-but-unencrypted payload. Returns null when the bytes are
    /// not a compressed stream.
    /// </summary>
    public static string? TryInflate(byte[] payload)
    {
        if (payload is null || payload.Length < 8) return null;

        var inflated = Inflate(payload);
        if (inflated is null) return null;

        var text = Encoding.UTF8.GetString(inflated);
        return text.Contains('[') && text.Contains('(') ? text : null;
    }

    /// <summary>
    /// Try to decrypt a QRC payload. Returns null (never throws) when the data is not
    /// valid QRC, so callers can fall back to the plain LRC payload.
    /// </summary>
    public static string? TryDecrypt(byte[] ciphertext)
    {
        if (ciphertext is null || ciphertext.Length < 16)
        {
            LastDiagnostic = $"too short ({ciphertext?.Length ?? 0} bytes)";
            return null;
        }

        if (ciphertext.Length % 8 != 0)
        {
            LastDiagnostic = $"not a whole number of 8-byte blocks ({ciphertext.Length} bytes)";
            return null;
        }

        try
        {
            var schedule = TripleDesKeySetup(QqKey, Decrypt);
            var plain = new byte[ciphertext.Length];

            for (int i = 0; i < ciphertext.Length; i += 8)
            {
                var block = TripleDesCrypt(ciphertext.AsSpan(i, 8), schedule);
                block.CopyTo(plain, i);
            }

            // A valid zlib stream starts with 0x78; anything else means the block
            // decryption produced noise.
            LastDiagnostic = $"decrypted {plain.Length} bytes, first={plain[0]:X2} {plain[1]:X2} {plain[2]:X2}";

            var inflated = Inflate(plain);
            if (inflated is null)
            {
                LastDiagnostic += "; inflate failed";
                return null;
            }

            var text = Encoding.UTF8.GetString(inflated);
            LastDiagnostic += $"; inflated {inflated.Length} bytes, head='{text[..Math.Min(60, text.Length)]}'";

            // The decrypted payload is QRC text; anything else means the key or the
            // format changed, and the caller should not use it.
            if (!text.Contains('[') || !text.Contains('('))
            {
                LastDiagnostic += "; not QRC-shaped";
                return null;
            }

            return text;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            LastDiagnostic = $"exception: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Compress-then-encrypt, used by the self-test to prove the pair is sound.</summary>
    public static byte[] EncryptForTest(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        // Pad to a whole number of blocks so the block loop is exact.
        var source = output.ToArray();
        int padded = (source.Length + 7) / 8 * 8;
        var buffer = new byte[padded];
        source.CopyTo(buffer, 0);

        var schedule = TripleDesKeySetup(QqKey, Encrypt);
        var result = new byte[padded];

        for (int i = 0; i < padded; i += 8)
        {
            var block = TripleDesCrypt(buffer.AsSpan(i, 8), schedule);
            block.CopyTo(result, i);
        }

        return result;
    }

    /// <summary>
    /// Inflate the decrypted bytes. QRC uses a zlib stream, but raw deflate is tried as
    /// well so a change on the server side degrades to a fallback rather than a failure.
    /// </summary>
    private static byte[]? Inflate(byte[] data)
    {
        foreach (bool zlib in new[] { true, false })
        {
            try
            {
                using var input = new MemoryStream(data);
                using var output = new MemoryStream();

                Stream decompressor = zlib
                    ? new ZLibStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress);

                using (decompressor)
                {
                    decompressor.CopyTo(output);
                }

                var bytes = output.ToArray();
                if (bytes.Length > 0) return bytes;
            }
            catch (InvalidDataException)
            {
                // Try the next wrapper.
            }
        }

        return null;
    }

    // ---- DES core ------------------------------------------------------
    //
    // Ported from the reference implementations. Long is used deliberately: the
    // reference code relies on 64-bit integers, and 32-bit arithmetic would make
    // several of the shifts sign-extend where they must not.

    private static readonly int[][] Sbox =
    {
        new[] { 14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7, 0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,
                4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0, 15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13 },
        new[] { 15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10, 3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,
                0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15, 13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9 },
        new[] { 10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8, 13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,
                13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7, 1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12 },
        new[] { 7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15, 13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,
                10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4, 3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14 },
        new[] { 2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9, 14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,
                4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14, 11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3 },
        new[] { 12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11, 10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,
                9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6, 4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13 },
        new[] { 4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1, 13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,
                1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2, 6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12 },
        new[] { 13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7, 1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,
                7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8, 2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11 },
    };

    private static readonly int[] KeyRndShift = { 1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1 };

    private static readonly int[] KeyPermC =
    {
        56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35,
    };

    private static readonly int[] KeyPermD =
    {
        62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3,
    };

    /// <summary>
    /// PC-2. The second half is indexed as <c>value - 27</c> in the schedule below, which
    /// is the deviation from standard DES.
    /// </summary>
    private static readonly int[] KeyCompression =
    {
        13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,
        40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31,
    };

    /// <summary>
    /// Extract bit <paramref name="b"/> of the 8-byte block into position
    /// <paramref name="c"/>.
    /// <para>
    /// The cast to <see cref="long"/> must happen <b>before</b> the shift: in 32-bit
    /// arithmetic <c>1 &lt;&lt; 31</c> is negative, whereas the reference (with 64-bit
    /// integers) yields a positive value. Getting this wrong still round-trips — the
    /// error is symmetric — but produces garbage against real server data.
    /// </para>
    /// </summary>
    private static long Bitnum(byte[] a, int b, int c) =>
        (long)((a[(b / 32) * 4 + 3 - (b % 32) / 8] >> (7 - b % 8)) & 1) << c;

    private static long BitnumIntr(long a, int b, int c) => ((a >> (31 - b)) & 1) << c;

    private static long BitnumIntl(long a, int b, int c) =>
        (long)((((ulong)(uint)a << b) & 0x80000000UL) >> c);

    private static int SboxBit(int a) => (a & 32) | ((a & 31) >> 1) | ((a & 1) << 4);

    private static (long S0, long S1) InitialPermutation(ReadOnlySpan<byte> input)
    {
        // Materialised once: the permutation touches the bytes 64 times per block, and
        // a song runs to roughly a thousand blocks.
        var a = input.ToArray();

        return (Bitnum(a, 57, 31) | Bitnum(a, 49, 30) | Bitnum(a, 41, 29) |
                Bitnum(a, 33, 28) | Bitnum(a, 25, 27) | Bitnum(a, 17, 26) |
                Bitnum(a, 9, 25) | Bitnum(a, 1, 24) | Bitnum(a, 59, 23) |
                Bitnum(a, 51, 22) | Bitnum(a, 43, 21) | Bitnum(a, 35, 20) |
                Bitnum(a, 27, 19) | Bitnum(a, 19, 18) | Bitnum(a, 11, 17) |
                Bitnum(a, 3, 16) | Bitnum(a, 61, 15) | Bitnum(a, 53, 14) |
                Bitnum(a, 45, 13) | Bitnum(a, 37, 12) | Bitnum(a, 29, 11) |
                Bitnum(a, 21, 10) | Bitnum(a, 13, 9) | Bitnum(a, 5, 8) |
                Bitnum(a, 63, 7) | Bitnum(a, 55, 6) | Bitnum(a, 47, 5) |
                Bitnum(a, 39, 4) | Bitnum(a, 31, 3) | Bitnum(a, 23, 2) |
                Bitnum(a, 15, 1) | Bitnum(a, 7, 0),

                Bitnum(a, 56, 31) | Bitnum(a, 48, 30) | Bitnum(a, 40, 29) |
                Bitnum(a, 32, 28) | Bitnum(a, 24, 27) | Bitnum(a, 16, 26) |
                Bitnum(a, 8, 25) | Bitnum(a, 0, 24) | Bitnum(a, 58, 23) |
                Bitnum(a, 50, 22) | Bitnum(a, 42, 21) | Bitnum(a, 34, 20) |
                Bitnum(a, 26, 19) | Bitnum(a, 18, 18) | Bitnum(a, 10, 17) |
                Bitnum(a, 2, 16) | Bitnum(a, 60, 15) | Bitnum(a, 52, 14) |
                Bitnum(a, 44, 13) | Bitnum(a, 36, 12) | Bitnum(a, 28, 11) |
                Bitnum(a, 20, 10) | Bitnum(a, 12, 9) | Bitnum(a, 4, 8) |
                Bitnum(a, 62, 7) | Bitnum(a, 54, 6) | Bitnum(a, 46, 5) |
                Bitnum(a, 38, 4) | Bitnum(a, 30, 3) | Bitnum(a, 22, 2) |
                Bitnum(a, 14, 1) | Bitnum(a, 6, 0));
    }

    private static byte[] InversePermutation(long s0, long s1)
    {
        var d = new byte[8];
        d[3] = (byte)(BitnumIntr(s1, 7, 7) | BitnumIntr(s0, 7, 6) | BitnumIntr(s1, 15, 5) | BitnumIntr(s0, 15, 4) |
                       BitnumIntr(s1, 23, 3) | BitnumIntr(s0, 23, 2) | BitnumIntr(s1, 31, 1) | BitnumIntr(s0, 31, 0));
        d[2] = (byte)(BitnumIntr(s1, 6, 7) | BitnumIntr(s0, 6, 6) | BitnumIntr(s1, 14, 5) | BitnumIntr(s0, 14, 4) |
                       BitnumIntr(s1, 22, 3) | BitnumIntr(s0, 22, 2) | BitnumIntr(s1, 30, 1) | BitnumIntr(s0, 30, 0));
        d[1] = (byte)(BitnumIntr(s1, 5, 7) | BitnumIntr(s0, 5, 6) | BitnumIntr(s1, 13, 5) | BitnumIntr(s0, 13, 4) |
                       BitnumIntr(s1, 21, 3) | BitnumIntr(s0, 21, 2) | BitnumIntr(s1, 29, 1) | BitnumIntr(s0, 29, 0));
        d[0] = (byte)(BitnumIntr(s1, 4, 7) | BitnumIntr(s0, 4, 6) | BitnumIntr(s1, 12, 5) | BitnumIntr(s0, 12, 4) |
                       BitnumIntr(s1, 20, 3) | BitnumIntr(s0, 20, 2) | BitnumIntr(s1, 28, 1) | BitnumIntr(s0, 28, 0));
        d[7] = (byte)(BitnumIntr(s1, 3, 7) | BitnumIntr(s0, 3, 6) | BitnumIntr(s1, 11, 5) | BitnumIntr(s0, 11, 4) |
                       BitnumIntr(s1, 19, 3) | BitnumIntr(s0, 19, 2) | BitnumIntr(s1, 27, 1) | BitnumIntr(s0, 27, 0));
        d[6] = (byte)(BitnumIntr(s1, 2, 7) | BitnumIntr(s0, 2, 6) | BitnumIntr(s1, 10, 5) | BitnumIntr(s0, 10, 4) |
                       BitnumIntr(s1, 18, 3) | BitnumIntr(s0, 18, 2) | BitnumIntr(s1, 26, 1) | BitnumIntr(s0, 26, 0));
        d[5] = (byte)(BitnumIntr(s1, 1, 7) | BitnumIntr(s0, 1, 6) | BitnumIntr(s1, 9, 5) | BitnumIntr(s0, 9, 4) |
                       BitnumIntr(s1, 17, 3) | BitnumIntr(s0, 17, 2) | BitnumIntr(s1, 25, 1) | BitnumIntr(s0, 25, 0));
        d[4] = (byte)(BitnumIntr(s1, 0, 7) | BitnumIntr(s0, 0, 6) | BitnumIntr(s1, 8, 5) | BitnumIntr(s0, 8, 4) |
                       BitnumIntr(s1, 16, 3) | BitnumIntr(s0, 16, 2) | BitnumIntr(s1, 24, 1) | BitnumIntr(s0, 24, 0));
        return d;
    }

    private static long F(long state, int[] key)
    {
        long t1 = BitnumIntl(state, 31, 0) | (long)(((ulong)(uint)state & 0xF0000000UL) >> 1) |
                  BitnumIntl(state, 4, 5) | BitnumIntl(state, 3, 6) |
                  (long)(((ulong)(uint)state & 0x0F000000UL) >> 3) |
                  BitnumIntl(state, 8, 11) | BitnumIntl(state, 7, 12) |
                  (long)(((ulong)(uint)state & 0x00F00000UL) >> 5) |
                  BitnumIntl(state, 12, 17) | BitnumIntl(state, 11, 18) |
                  (long)(((ulong)(uint)state & 0x000F0000UL) >> 7) |
                  BitnumIntl(state, 16, 23);

        long t2 = BitnumIntl(state, 15, 0) | (long)(((ulong)(uint)state & 0x0000F000UL) << 15) |
                  BitnumIntl(state, 20, 5) | BitnumIntl(state, 19, 6) |
                  (long)(((ulong)(uint)state & 0x00000F00UL) << 13) |
                  BitnumIntl(state, 24, 11) | BitnumIntl(state, 23, 12) |
                  (long)(((ulong)(uint)state & 0x000000F0UL) << 11) |
                  BitnumIntl(state, 28, 17) | BitnumIntl(state, 27, 18) |
                  (long)(((ulong)(uint)state & 0x0000000FUL) << 9) |
                  BitnumIntl(state, 0, 23);

        int k0 = (int)(((t1 >> 24) & 0xFF) ^ (uint)key[0]);
        int k1 = (int)(((t1 >> 16) & 0xFF) ^ (uint)key[1]);
        int k2 = (int)(((t1 >> 8) & 0xFF) ^ (uint)key[2]);
        int k3 = (int)(((t2 >> 24) & 0xFF) ^ (uint)key[3]);
        int k4 = (int)(((t2 >> 16) & 0xFF) ^ (uint)key[4]);
        int k5 = (int)(((t2 >> 8) & 0xFF) ^ (uint)key[5]);

        long state2 =
            ((long)Sbox[0][SboxBit(k0 >> 2)] << 28) |
            ((long)Sbox[1][SboxBit(((k0 & 0x03) << 4) | (k1 >> 4))] << 24) |
            ((long)Sbox[2][SboxBit(((k1 & 0x0F) << 2) | (k2 >> 6))] << 20) |
            ((long)Sbox[3][SboxBit(k2 & 0x3F)] << 16) |
            ((long)Sbox[4][SboxBit(k3 >> 2)] << 12) |
            ((long)Sbox[5][SboxBit(((k3 & 0x03) << 4) | (k4 >> 4))] << 8) |
            ((long)Sbox[6][SboxBit(((k4 & 0x0F) << 2) | (k5 >> 6))] << 4) |
            (uint)Sbox[7][SboxBit(k5 & 0x3F)];

        return BitnumIntl(state2, 15, 0) | BitnumIntl(state2, 6, 1) | BitnumIntl(state2, 19, 2) |
               BitnumIntl(state2, 20, 3) | BitnumIntl(state2, 28, 4) | BitnumIntl(state2, 11, 5) |
               BitnumIntl(state2, 27, 6) | BitnumIntl(state2, 16, 7) | BitnumIntl(state2, 0, 8) |
               BitnumIntl(state2, 14, 9) | BitnumIntl(state2, 22, 10) | BitnumIntl(state2, 25, 11) |
               BitnumIntl(state2, 4, 12) | BitnumIntl(state2, 17, 13) | BitnumIntl(state2, 30, 14) |
               BitnumIntl(state2, 9, 15) | BitnumIntl(state2, 1, 16) | BitnumIntl(state2, 7, 17) |
               BitnumIntl(state2, 23, 18) | BitnumIntl(state2, 13, 19) | BitnumIntl(state2, 31, 20) |
               BitnumIntl(state2, 26, 21) | BitnumIntl(state2, 2, 22) | BitnumIntl(state2, 8, 23) |
               BitnumIntl(state2, 18, 24) | BitnumIntl(state2, 12, 25) | BitnumIntl(state2, 29, 26) |
               BitnumIntl(state2, 5, 27) | BitnumIntl(state2, 21, 28) | BitnumIntl(state2, 10, 29) |
               BitnumIntl(state2, 3, 30) | BitnumIntl(state2, 24, 31);
    }

    private static byte[] Crypt(ReadOnlySpan<byte> input, int[][] key)
    {
        var (s0, s1) = InitialPermutation(input);

        for (int idx = 0; idx < 15; idx++)
        {
            long prevS1 = s1;
            s1 = F(s1, key[idx]) ^ s0;
            s0 = prevS1;
        }

        s0 = F(s1, key[15]) ^ s0;
        return InversePermutation(s0, s1);
    }

    private static int[][] KeySchedule(ReadOnlySpan<byte> key, int mode)
    {
        var schedule = new int[16][];
        for (int i = 0; i < 16; i++) schedule[i] = new int[6];

        var keyBytes = key[..8].ToArray();

        long c = 0, d = 0;
        for (int i = 0; i < 28; i++) c |= Bitnum(keyBytes, KeyPermC[i], 31 - i);
        for (int i = 0; i < 28; i++) d |= Bitnum(keyBytes, KeyPermD[i], 31 - i);

        for (int i = 0; i < 16; i++)
        {
            int shift = KeyRndShift[i];

            c = (long)((((uint)c << shift) | ((uint)c >> (28 - shift))) & 0xFFFFFFF0u);
            d = (long)((((uint)d << shift) | ((uint)d >> (28 - shift))) & 0xFFFFFFF0u);

            int togen = mode == Decrypt ? 15 - i : i;

            for (int j = 0; j < 6; j++) schedule[togen][j] = 0;

            for (int j = 0; j < 24; j++)
            {
                schedule[togen][j / 8] |= (int)BitnumIntr(c, KeyCompression[j], 7 - (j % 8));
            }

            for (int j = 24; j < 48; j++)
            {
                // The -27 offset is QQ's deviation from standard DES.
                schedule[togen][j / 8] |= (int)BitnumIntr(d, KeyCompression[j] - 27, 7 - (j % 8));
            }
        }

        return schedule;
    }

    private static int[][][] TripleDesKeySetup(byte[] key, int mode)
    {
        if (mode == Encrypt)
        {
            return new[]
            {
                KeySchedule(key.AsSpan(0, 8), Encrypt),
                KeySchedule(key.AsSpan(8, 8), Decrypt),
                KeySchedule(key.AsSpan(16, 8), Encrypt),
            };
        }

        return new[]
        {
            KeySchedule(key.AsSpan(16, 8), Decrypt),
            KeySchedule(key.AsSpan(8, 8), Encrypt),
            KeySchedule(key.AsSpan(0, 8), Decrypt),
        };
    }

    private static byte[] TripleDesCrypt(ReadOnlySpan<byte> data, int[][][] key)
    {
        var block = data.ToArray();

        for (int i = 0; i < 3; i++)
        {
            block = Crypt(block, key[i]);
        }

        return block;
    }
}
