using System.Security.Cryptography;
using System.Text;

namespace SimpleRadius.Core.Crypto;

/// <summary>
/// Centralised password utilities — zero external NuGet dependencies.
///
/// Storage strategy:
///   PAP       → PBKDF2-SHA256 hash stored as "pbkdf2$iterations$salt$hash" (all base64).
///               On auth: decrypt PAP cipher → verify against PBKDF2 hash.
///
///   CHAP /
///   MSCHAPv2  → NT Hash (MD4 of UTF-16LE password) stored as hex.
///               These protocols need a password-equivalent, so we store
///               the NT Hash alongside the PBKDF2 hash at account creation.
///
/// Migration: if a stored value is plain-text (Phase 1 users.json),
/// it is automatically upgraded to PBKDF2 + NT Hash on first successful auth.
/// </summary>
public static class PasswordHelper
{
    // ── PBKDF2-SHA256 ─────────────────────────────────────────────────────────
    private const int Iterations  = 310_000;   // OWASP 2023 recommendation for PBKDF2-SHA256
    private const int SaltBytes   = 16;
    private const int HashBytes    = 32;
    private const string Prefix   = "pbkdf2$";

    /// <summary>Hash a plain-text password with PBKDF2-SHA256.</summary>
    public static string HashPassword(string plainText)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(plainText, salt, Iterations, HashBytes);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Verify a plain-text password against a stored PBKDF2 string.</summary>
    public static bool VerifyPassword(string plainText, string stored)
    {
        try
        {
            if (!stored.StartsWith(Prefix)) return false;
            var parts = stored[Prefix.Length..].Split('$');
            if (parts.Length != 3) return false;

            int    iterations = int.Parse(parts[0]);
            byte[] salt       = Convert.FromBase64String(parts[1]);
            byte[] expected   = Convert.FromBase64String(parts[2]);
            byte[] actual     = Pbkdf2(plainText, salt, iterations, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    /// <summary>Returns true if the value is a PBKDF2 hash (Phase 2+).</summary>
    public static bool IsHashed(string value)
        => value.StartsWith(Prefix);

    // ── NT Hash (MD4 of UTF-16LE password) ───────────────────────────────────
    /// <summary>
    /// Compute the NT Hash of a password.
    /// NT Hash = MD4( UTF-16LE(password) )
    /// Required for CHAP and MSCHAPv2 verification.
    /// </summary>
    public static byte[] NtHash(string password)
        => Md4.Hash(Encoding.Unicode.GetBytes(password));   // Unicode = UTF-16LE on .NET

    // ── Helpers ───────────────────────────────────────────────────────────────
    public static string ToHex(byte[] data)
        => Convert.ToHexString(data).ToLowerInvariant();

    public static byte[] FromHex(string hex)
        => Convert.FromHexString(hex);

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations, int outputLen)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt, iterations,
            HashAlgorithmName.SHA256,
            outputLen);
}

/// <summary>
/// Pure-managed MD4 (RFC 1320).
/// Required for NT Hash — .NET's crypto library does not include MD4.
/// </summary>
internal static class Md4
{
    public static byte[] Hash(byte[] input)
    {
        int origLen = input.Length;
        int padLen  = ((origLen % 64) < 56) ? (56 - origLen % 64) : (120 - origLen % 64);
        var msg     = new byte[origLen + padLen + 8];
        Buffer.BlockCopy(input, 0, msg, 0, origLen);
        msg[origLen] = 0x80;
        ulong bitLen = (ulong)origLen * 8;
        for (int i = 0; i < 8; i++) msg[origLen + padLen + i] = (byte)(bitLen >> (8 * i));

        uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476;

        for (int off = 0; off < msg.Length; off += 64)
        {
            var X  = new uint[16];
            for (int i = 0; i < 16; i++) X[i] = BitConverter.ToUInt32(msg, off + i * 4);
            uint aa = a, bb = b, cc = c, dd = d;

            static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
            static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
            static uint H(uint x, uint y, uint z) => x ^ y ^ z;
            static uint RL(uint x, int n) => (x << n) | (x >> (32 - n));

            void R1(ref uint v, uint w, uint x, uint y, uint k, int s) => v = RL(v + F(w,x,y) + k, s);
            void R2(ref uint v, uint w, uint x, uint y, uint k, int s) => v = RL(v + G(w,x,y) + k + 0x5A827999u, s);
            void R3(ref uint v, uint w, uint x, uint y, uint k, int s) => v = RL(v + H(w,x,y) + k + 0x6ED9EBA1u, s);

            R1(ref a,b,c,d,X[0], 3);  R1(ref d,a,b,c,X[1], 7);  R1(ref c,d,a,b,X[2], 11); R1(ref b,c,d,a,X[3], 19);
            R1(ref a,b,c,d,X[4], 3);  R1(ref d,a,b,c,X[5], 7);  R1(ref c,d,a,b,X[6], 11); R1(ref b,c,d,a,X[7], 19);
            R1(ref a,b,c,d,X[8], 3);  R1(ref d,a,b,c,X[9], 7);  R1(ref c,d,a,b,X[10],11); R1(ref b,c,d,a,X[11],19);
            R1(ref a,b,c,d,X[12],3);  R1(ref d,a,b,c,X[13],7);  R1(ref c,d,a,b,X[14],11); R1(ref b,c,d,a,X[15],19);

            R2(ref a,b,c,d,X[0], 3);  R2(ref d,a,b,c,X[4], 5);  R2(ref c,d,a,b,X[8], 9);  R2(ref b,c,d,a,X[12],13);
            R2(ref a,b,c,d,X[1], 3);  R2(ref d,a,b,c,X[5], 5);  R2(ref c,d,a,b,X[9], 9);  R2(ref b,c,d,a,X[13],13);
            R2(ref a,b,c,d,X[2], 3);  R2(ref d,a,b,c,X[6], 5);  R2(ref c,d,a,b,X[10],9);  R2(ref b,c,d,a,X[14],13);
            R2(ref a,b,c,d,X[3], 3);  R2(ref d,a,b,c,X[7], 5);  R2(ref c,d,a,b,X[11],9);  R2(ref b,c,d,a,X[15],13);

            R3(ref a,b,c,d,X[0], 3);  R3(ref d,a,b,c,X[8], 9);  R3(ref c,d,a,b,X[4], 11); R3(ref b,c,d,a,X[12],15);
            R3(ref a,b,c,d,X[2], 3);  R3(ref d,a,b,c,X[10],9);  R3(ref c,d,a,b,X[6], 11); R3(ref b,c,d,a,X[14],15);
            R3(ref a,b,c,d,X[1], 3);  R3(ref d,a,b,c,X[9], 9);  R3(ref c,d,a,b,X[5], 11); R3(ref b,c,d,a,X[13],15);
            R3(ref a,b,c,d,X[3], 3);  R3(ref d,a,b,c,X[11],9);  R3(ref c,d,a,b,X[7], 11); R3(ref b,c,d,a,X[15],15);

            a += aa; b += bb; c += cc; d += dd;
        }

        var result = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(a), 0, result, 0,  4);
        Buffer.BlockCopy(BitConverter.GetBytes(b), 0, result, 4,  4);
        Buffer.BlockCopy(BitConverter.GetBytes(c), 0, result, 8,  4);
        Buffer.BlockCopy(BitConverter.GetBytes(d), 0, result, 12, 4);
        return result;
    }
}
