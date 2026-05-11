using System.Security.Cryptography;
using System.Text;
using SimpleRadius.Core.Crypto;
using SimpleRadius.Core.Storage;

namespace SimpleRadius.Core.Auth;

/// <summary>
/// MSCHAPv2 (Microsoft CHAP Version 2) — RFC 2759.
///
/// This is the inner auth method used inside PEAP tunnels for WPA2-Enterprise.
/// It can also be used standalone (MS-CHAP-V2 VSA in Access-Request).
///
/// Full flow:
///   1. NAS sends Access-Request with:
///        MS-CHAP-Challenge  (16 bytes: authenticator challenge)
///        MS-CHAP2-Response  (50 bytes: peer challenge + NT-Response)
///
///   2. Server computes the expected NT-Response and compares.
///
///   3. On success, server computes the Authenticator-Response string
///      (42 chars: "S=<40 hex chars>") and sends it back so the
///      client can verify the SERVER is also legitimate (mutual auth).
///
/// NT-Response derivation (RFC 2759 §8):
///   ChallengeHash = SHA1(PeerChallenge | AuthChallenge | Username)[0..7]
///   NT-Response   = ChallengeResponse(ChallengeHash, NT-Hash)
///
/// ChallengeResponse (DES-based):
///   Pad NT-Hash (16 bytes) to 21 bytes with zeros.
///   Split into three 7-byte DES keys.
///   NT-Response = DES(key1, hash) || DES(key2, hash) || DES(key3, hash)
/// </summary>
public static class MsChapV2Handler
{
    // ── Main verification entry point ─────────────────────────────────────────
    public static bool Authenticate(
        string   username,
        byte[]   authChallenge,     // 16 bytes from MS-CHAP-Challenge attribute
        byte[]   peerChallenge,     // 16 bytes from MS-CHAP2-Response[0..15]
        byte[]   ntResponse,        // 24 bytes from MS-CHAP2-Response[24..47]
        UserStore users,
        out string authenticatorResponse,   // "S=<40 hex>" sent back to client
        out string reason)
    {
        authenticatorResponse = "";
        reason                = "";

        var ntHash = users.GetNtHash(username);
        if (ntHash == null)
        {
            reason = "Unknown user or no NT Hash";
            return false;
        }

        // Step 1 — ChallengeHash = SHA1(PeerChallenge|AuthChallenge|Username)[0..7]
        var challengeHash = GenerateChallengeHash(peerChallenge, authChallenge, username);

        // Step 2 — Expected NT-Response
        var expectedResponse = ChallengeResponse(challengeHash, ntHash);

        // Step 3 — Constant-time compare
        if (!CryptographicOperations.FixedTimeEquals(expectedResponse, ntResponse))
        {
            reason = "NT-Response mismatch";
            return false;
        }

        // Step 4 — Generate Authenticator Response for mutual auth
        authenticatorResponse = GenerateAuthenticatorResponse(ntHash, ntResponse, peerChallenge, authChallenge, username);
        reason = "MSCHAPv2 OK";
        return true;
    }

    // ── ChallengeHash ─────────────────────────────────────────────────────────
    /// <summary>
    /// RFC 2759 §8.2:
    /// ChallengeHash = SHA1(PeerChallenge + AuthChallenge + UserName)[0..7]
    /// </summary>
    public static byte[] GenerateChallengeHash(byte[] peerChallenge, byte[] authChallenge, string username)
    {
        var buf = new byte[16 + 16 + username.Length];
        Buffer.BlockCopy(peerChallenge,  0, buf, 0,  16);
        Buffer.BlockCopy(authChallenge,  0, buf, 16, 16);
        Buffer.BlockCopy(Encoding.ASCII.GetBytes(username), 0, buf, 32, username.Length);
        var hash = SHA1.HashData(buf);
        return hash[..8];   // first 8 bytes only
    }

    // ── ChallengeResponse (DES) ───────────────────────────────────────────────
    /// <summary>
    /// RFC 2759 §8.5:
    /// Pad NT-Hash to 21 bytes. Split into 7-byte chunks. Run DES on each.
    /// </summary>
    public static byte[] ChallengeResponse(byte[] challenge8, byte[] ntHash16)
    {
        var padded = new byte[21];
        Buffer.BlockCopy(ntHash16, 0, padded, 0, 16);
        // bytes 16-20 = 0x00 (already zero from new byte[21])

        var result = new byte[24];
        DesEncrypt(challenge8, padded[0..7],   result, 0);
        DesEncrypt(challenge8, padded[7..14],  result, 8);
        DesEncrypt(challenge8, padded[14..21], result, 16);
        return result;
    }

    // ── Authenticator Response ────────────────────────────────────────────────
    /// <summary>
    /// RFC 2759 §8.7 — GenerateAuthenticatorResponse.
    /// Returns the "S=XXXXXXXX..." string the server sends back to the client.
    /// The client verifies this to confirm the server knows the password (mutual auth).
    /// </summary>
    public static string GenerateAuthenticatorResponse(
        byte[] ntHash, byte[] ntResponse,
        byte[] peerChallenge, byte[] authChallenge, string username)
    {
        // Magic constants from RFC 2759 §8.7
        byte[] magic1 =
        {
            0x4D,0x61,0x67,0x69,0x63,0x20,0x73,0x65,0x72,0x76,
            0x65,0x72,0x20,0x74,0x6F,0x20,0x63,0x6C,0x69,0x65,
            0x6E,0x74,0x20,0x73,0x69,0x67,0x6E,0x69,0x6E,0x67,
            0x20,0x63,0x6F,0x6E,0x73,0x74,0x61,0x6E,0x74
        };
        byte[] magic2 =
        {
            0x50,0x61,0x64,0x20,0x74,0x6F,0x20,0x6D,0x61,0x6B,
            0x65,0x20,0x69,0x74,0x20,0x64,0x6F,0x20,0x6D,0x6F,
            0x72,0x65,0x20,0x74,0x68,0x61,0x6E,0x20,0x6F,0x6E,
            0x65,0x20,0x69,0x74,0x65,0x72,0x61,0x74,0x69,0x6F,
            0x6E
        };

        // PasswordHashHash = MD4(NT-Hash)
        var hashHash = Md4.Hash(ntHash);

        // Digest1 = SHA1(hashHash + ntResponse + magic1)
        var buf1 = Combine(hashHash, ntResponse, magic1);
        var digest1 = SHA1.HashData(buf1);

        // ChallengeHash
        var challengeHash = GenerateChallengeHash(peerChallenge, authChallenge, username);

        // Digest2 = SHA1(digest1 + challengeHash + magic2)
        var buf2 = Combine(digest1, challengeHash, magic2);
        var digest2 = SHA1.HashData(buf2);

        return "S=" + Convert.ToHexString(digest2).ToUpperInvariant();
    }

    // ── DES helper ────────────────────────────────────────────────────────────
    /// <summary>
    /// Expand a 7-byte key to an 8-byte DES key with odd parity,
    /// then encrypt 8 bytes of data.
    /// </summary>
    private static void DesEncrypt(byte[] data8, byte[] key7, byte[] output, int outOffset)
    {
        var key8 = new byte[8];
        key8[0] = (byte)(key7[0] >> 1);
        key8[1] = (byte)(((key7[0] & 0x01) << 6) | (key7[1] >> 2));
        key8[2] = (byte)(((key7[1] & 0x03) << 5) | (key7[2] >> 3));
        key8[3] = (byte)(((key7[2] & 0x07) << 4) | (key7[3] >> 4));
        key8[4] = (byte)(((key7[3] & 0x0F) << 3) | (key7[4] >> 5));
        key8[5] = (byte)(((key7[4] & 0x1F) << 2) | (key7[5] >> 6));
        key8[6] = (byte)(((key7[5] & 0x3F) << 1) | (key7[6] >> 7));
        key8[7] = (byte)(key7[6] & 0x7F);

        // Set odd parity on each byte
        for (int i = 0; i < 8; i++)
        {
            key8[i] <<= 1;
            int bits = 0;
            for (int b = 1; b < 8; b++) if ((key8[i] & (1 << b)) != 0) bits++;
            if (bits % 2 == 0) key8[i] |= 1;
        }

        using var des = DES.Create();
        des.Mode    = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key     = key8;

        using var enc    = des.CreateEncryptor();
        var encrypted = enc.TransformFinalBlock(data8, 0, 8);
        Buffer.BlockCopy(encrypted, 0, output, outOffset, 8);
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        var buf   = new byte[total];
        int pos   = 0;
        foreach (var a in arrays) { Buffer.BlockCopy(a, 0, buf, pos, a.Length); pos += a.Length; }
        return buf;
    }
}
