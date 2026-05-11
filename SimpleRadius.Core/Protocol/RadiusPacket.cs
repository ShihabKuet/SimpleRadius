using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SimpleRadius.Core.Protocol;

// ── RFC 2865 §3  Packet codes ────────────────────────────────────────────────
public enum RadiusCode : byte
{
    AccessRequest      = 1,
    AccessAccept       = 2,
    AccessReject       = 3,
    AccountingRequest  = 4,
    AccountingResponse = 5,
    AccessChallenge    = 11,
    StatusServer       = 12,
    StatusClient       = 13,
}

// ── RFC 2865 §5  Standard attribute types ────────────────────────────────────
public enum RadiusAttributeType : byte
{
    UserName              = 1,
    UserPassword          = 2,   // PAP, XOR-encrypted
    ChapPassword          = 3,
    NasIpAddress          = 4,
    NasPort               = 5,
    ServiceType           = 6,
    FramedProtocol        = 7,
    FramedIpAddress       = 8,
    FramedIpNetmask       = 9,
    ReplyMessage          = 18,
    ChapChallenge         = 60,   // explicit CHAP challenge (RFC 2865 §5.40)
    State                 = 24,
    SessionTimeout        = 27,
    IdleTimeout           = 28,
    CalledStationId       = 30,
    CallingStationId      = 31,
    NasIdentifier         = 32,
    AcctStatusType        = 40,
    AcctInputOctets       = 42,
    AcctOutputOctets      = 43,
    AcctSessionId         = 44,
    AcctSessionTime       = 46,
    AcctTerminateCause    = 49,
    NasPortType           = 61,
    TunnelType            = 64,   // VLAN tagging
    TunnelMediumType      = 65,
    TunnelPrivateGroupId  = 81,
    EapMessage            = 79,
    MessageAuthenticator  = 80,
    VendorSpecific        = 26,   // RFC 2865 §5.26 — carries MSCHAPv2/Microsoft VSAs
}

// ── Single RADIUS attribute (Type-Length-Value) ───────────────────────────────
public sealed class RadiusAttribute
{
    public RadiusAttributeType Type  { get; }
    public byte[]              Value { get; }

    public RadiusAttribute(RadiusAttributeType type, byte[] value)
    {
        Type  = type;
        Value = value;
    }

    // Convenience constructors
    public RadiusAttribute(RadiusAttributeType type, string value)
        : this(type, Encoding.UTF8.GetBytes(value)) { }

    public RadiusAttribute(RadiusAttributeType type, IPAddress value)
        : this(type, value.GetAddressBytes()) { }

    public RadiusAttribute(RadiusAttributeType type, uint value)
        : this(type, new[] {
            (byte)(value >> 24), (byte)(value >> 16),
            (byte)(value >> 8),  (byte)(value)
        }) { }

    // Decode helpers
    public string   AsString()  => Encoding.UTF8.GetString(Value);
    public string   AsIpString() => Value.Length == 4
        ? $"{Value[0]}.{Value[1]}.{Value[2]}.{Value[3]}" : AsString();
    public uint     AsUInt32()  => Value.Length >= 4
        ? (uint)((Value[0] << 24) | (Value[1] << 16) | (Value[2] << 8) | Value[3]) : 0;

    // Serialise to wire format
    public byte[] Encode()
    {
        // Total length = 1 (type) + 1 (length byte) + value bytes
        var buf = new byte[2 + Value.Length];
        buf[0] = (byte)Type;
        buf[1] = (byte)buf.Length;
        Buffer.BlockCopy(Value, 0, buf, 2, Value.Length);
        return buf;
    }

    // Deserialise from wire buffer at given offset; returns null on error
    public static RadiusAttribute? Decode(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) return null;
        var type   = (RadiusAttributeType)data[offset];
        int length = data[offset + 1];
        if (length < 2 || offset + length > data.Length) return null;
        var value = new byte[length - 2];
        Buffer.BlockCopy(data, offset + 2, value, 0, value.Length);
        return new RadiusAttribute(type, value);
    }
}

// ── Full RADIUS packet ────────────────────────────────────────────────────────
public sealed class RadiusPacket
{
    public RadiusCode             Code          { get; set; }
    public byte                   Identifier    { get; set; }
    public byte[]                 Authenticator { get; set; } = new byte[16];
    public List<RadiusAttribute>  Attributes    { get; }      = new();

    // ── Convenience accessors ─────────────────────────────────────────────────
    public string? UserName    => GetAttribute(RadiusAttributeType.UserName)?.AsString();
    public string? NasIdentifier => GetAttribute(RadiusAttributeType.NasIdentifier)?.AsString();

    public RadiusAttribute? GetAttribute(RadiusAttributeType type)
        => Attributes.FirstOrDefault(a => a.Type == type);

    public IEnumerable<RadiusAttribute> GetAttributes(RadiusAttributeType type)
        => Attributes.Where(a => a.Type == type);

    // ── Wire parsing ──────────────────────────────────────────────────────────
    /// <summary>
    /// Parse a raw UDP payload into a RadiusPacket.
    /// Returns null if the buffer is malformed.
    /// </summary>
    public static RadiusPacket? Parse(byte[] data)
    {
        if (data.Length < 20) return null;

        int declaredLength = (data[2] << 8) | data[3];
        if (declaredLength < 20 || declaredLength > data.Length) return null;

        var pkt = new RadiusPacket
        {
            Code       = (RadiusCode)data[0],
            Identifier = data[1],
        };
        Buffer.BlockCopy(data, 4, pkt.Authenticator, 0, 16);

        int offset = 20;
        while (offset < declaredLength)
        {
            var attr = RadiusAttribute.Decode(data, offset);
            if (attr == null) break;
            pkt.Attributes.Add(attr);
            offset += 2 + attr.Value.Length;
        }
        return pkt;
    }

    // ── Wire encoding ─────────────────────────────────────────────────────────
    public byte[] Encode()
    {
        var attrBytes = Attributes.SelectMany(a => a.Encode()).ToArray();
        int length    = 20 + attrBytes.Length;
        var buf       = new byte[length];

        buf[0] = (byte)Code;
        buf[1] = Identifier;
        buf[2] = (byte)(length >> 8);
        buf[3] = (byte)(length & 0xFF);
        Buffer.BlockCopy(Authenticator, 0, buf, 4, 16);
        Buffer.BlockCopy(attrBytes,     0, buf, 20, attrBytes.Length);
        return buf;
    }

    // ── Response authenticator ────────────────────────────────────────────────
    /// <summary>
    /// Computes and sets the response authenticator per RFC 2865 §3:
    /// MD5( Code + ID + Length + RequestAuth + Attributes + SharedSecret )
    /// </summary>
    public void SetResponseAuthenticator(byte[] requestAuthenticator, string sharedSecret)
    {
        var attrBytes   = Attributes.SelectMany(a => a.Encode()).ToArray();
        int length      = 20 + attrBytes.Length;
        var secretBytes = Encoding.UTF8.GetBytes(sharedSecret);

        // Build the buffer to hash
        var buf = new byte[length + secretBytes.Length];
        buf[0]  = (byte)Code;
        buf[1]  = Identifier;
        buf[2]  = (byte)(length >> 8);
        buf[3]  = (byte)(length & 0xFF);
        Buffer.BlockCopy(requestAuthenticator, 0, buf, 4,      16);
        Buffer.BlockCopy(attrBytes,            0, buf, 20,     attrBytes.Length);
        Buffer.BlockCopy(secretBytes,          0, buf, length, secretBytes.Length);

        Authenticator = MD5.HashData(buf);
    }

    // ── PAP password decryption ───────────────────────────────────────────────
    /// <summary>
    /// Decrypts the User-Password attribute using the shared secret and
    /// the request authenticator, per RFC 2865 §5.2.
    ///
    /// c = p XOR MD5(secret + authenticator)        (first 16-byte block)
    /// c_n = p_n XOR MD5(secret + c_{n-1})          (subsequent blocks)
    /// </summary>
    public string? DecryptPapPassword(string sharedSecret, byte[] requestAuthenticator)
    {
        var pwdAttr = GetAttribute(RadiusAttributeType.UserPassword);
        if (pwdAttr == null || pwdAttr.Value.Length == 0) return null;

        var secret  = Encoding.UTF8.GetBytes(sharedSecret);
        var cipher  = pwdAttr.Value;
        var plain   = new byte[cipher.Length];
        byte[] prev = requestAuthenticator;

        for (int i = 0; i < cipher.Length; i += 16)
        {
            // hash_block = MD5(secret + prev_block)
            var hashInput = new byte[secret.Length + 16];
            Buffer.BlockCopy(secret, 0, hashInput, 0,             secret.Length);
            Buffer.BlockCopy(prev,   0, hashInput, secret.Length, 16);
            var hash = MD5.HashData(hashInput);

            int block = Math.Min(16, cipher.Length - i);
            for (int j = 0; j < block; j++)
                plain[i + j] = (byte)(cipher[i + j] ^ hash[j]);

            // Next block uses current cipher block as 'prev'
            prev = new byte[16];
            Buffer.BlockCopy(cipher, i, prev, 0, block);
        }

        // Password is null-padded to a 16-byte boundary — strip trailing nulls
        int nullIdx = Array.IndexOf(plain, (byte)0);
        return Encoding.UTF8.GetString(plain, 0, nullIdx < 0 ? plain.Length : nullIdx);
    }

    // ── Request authenticator validation ─────────────────────────────────────
    /// <summary>
    /// Verifies that an Access-Request came from a legitimate NAS by checking
    /// the request authenticator is 16 random bytes (non-zero check only;
    /// full HMAC validation uses Message-Authenticator attribute in Phase 2).
    /// </summary>
    public bool IsAuthenticatorValid()
        => Authenticator.Length == 16 && Authenticator.Any(b => b != 0);

    public override string ToString()
        => $"[{Code}] Id={Identifier} User={UserName ?? "(none)"}";
}
