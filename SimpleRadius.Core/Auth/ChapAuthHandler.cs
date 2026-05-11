using System.Security.Cryptography;
using SimpleRadius.Core.Protocol;
using SimpleRadius.Core.Storage;

namespace SimpleRadius.Core.Auth;

/// <summary>
/// CHAP (Challenge Handshake Authentication Protocol) — RFC 1994 / RFC 2865 §2.
///
/// Flow:
///   NAS generates a random 16-byte challenge and sends it to the client.
///   Client computes: MD5( CHAP-ID | Password | Challenge )
///   NAS forwards: Access-Request with CHAP-Password (17 bytes: ID + response)
///                 and CHAP-Challenge (16 bytes), or the challenge is in the
///                 Request Authenticator if CHAP-Challenge attribute is absent.
///
/// We verify by recomputing the expected response using the stored NT Hash
/// (plain password equivalent) and comparing it to what the NAS sent.
/// </summary>
public static class ChapAuthHandler
{
    /// <summary>
    /// Attempt CHAP authentication.
    /// Returns true if the CHAP response is valid.
    /// </summary>
    public static bool Authenticate(
        RadiusPacket request,
        UserStore    users,
        out string   username,
        out string   reason)
    {
        username = request.UserName ?? "";
        reason   = "";

        if (string.IsNullOrEmpty(username))
        {
            reason = "No username";
            return false;
        }

        // CHAP-Password attribute: 1 byte ID + 16 byte response = 17 bytes
        var chapPwdAttr = request.GetAttribute(RadiusAttributeType.ChapPassword);
        if (chapPwdAttr == null || chapPwdAttr.Value.Length != 17)
        {
            reason = "Missing or malformed CHAP-Password";
            return false;
        }

        byte   chapId       = chapPwdAttr.Value[0];
        byte[] chapResponse = chapPwdAttr.Value[1..];   // 16 bytes

        // CHAP-Challenge: prefer explicit attribute, fall back to Request Authenticator
        byte[] challenge;
        var chapChalAttr = request.GetAttribute(RadiusAttributeType.ChapChallenge);
        if (chapChalAttr != null && chapChalAttr.Value.Length >= 16)
            challenge = chapChalAttr.Value;
        else
            challenge = request.Authenticator;   // RFC 2865 §2 fallback

        // Look up user and get their NT Hash (used as the password equivalent)
        // NT Hash gives us a stable password surrogate without storing plain-text
        var ntHashBytes = users.GetNtHash(username);
        if (ntHashBytes == null)
        {
            reason = "Unknown user or no credential material";
            return false;
        }

        // Compute expected CHAP response:
        // MD5( CHAP-ID | NT-Hash | Challenge )
        // Note: some NAS send MD5(ID | Password | Challenge) with plain-text.
        // We use NT Hash as the password surrogate for Phase 2.
        // Phase 3 (PEAP) will carry the plain-text inside the TLS tunnel.
        byte[] expected = ComputeChapResponse(chapId, ntHashBytes, challenge);

        if (!CryptographicOperations.FixedTimeEquals(expected, chapResponse))
        {
            reason = "CHAP response mismatch";
            return false;
        }

        reason = "CHAP OK";
        return true;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static byte[] ComputeChapResponse(byte chapId, byte[] password, byte[] challenge)
    {
        // MD5( chapId || password || challenge )
        var buf = new byte[1 + password.Length + challenge.Length];
        buf[0] = chapId;
        Buffer.BlockCopy(password,  0, buf, 1,                password.Length);
        Buffer.BlockCopy(challenge, 0, buf, 1 + password.Length, challenge.Length);
        return MD5.HashData(buf);
    }
}
