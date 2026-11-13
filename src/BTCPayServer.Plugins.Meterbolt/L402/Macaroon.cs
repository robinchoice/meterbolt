#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Meterbolt.L402;

/// <summary>
/// Minimal macaroon (libmacaroons V2 binary format) with first-party caveats only.
/// Compatible with go-macaroon as used by lnd and Aperture.
/// </summary>
public sealed class Macaroon
{
    const byte FieldEos = 0;
    const byte FieldLocation = 1;
    const byte FieldIdentifier = 2;
    const byte FieldVerificationId = 4;
    const byte FieldSignature = 6;
    static readonly byte[] KeyGenerator = Encoding.ASCII.GetBytes("macaroons-key-generator");

    public string Location { get; }
    public byte[] Identifier { get; }
    public IReadOnlyList<string> Caveats => _caveats;
    public byte[] Signature { get; private set; }

    readonly List<string> _caveats = new();

    Macaroon(string location, byte[] identifier, byte[] signature)
    {
        Location = location;
        Identifier = identifier;
        Signature = signature;
    }

    public static Macaroon Create(byte[] rootKey, byte[] identifier, string location = "")
        => new(location, identifier, Hmac(DeriveKey(rootKey), identifier));

    public Macaroon AddFirstPartyCaveat(string caveat)
    {
        _caveats.Add(caveat);
        Signature = Hmac(Signature, Encoding.UTF8.GetBytes(caveat));
        return this;
    }

    /// <summary>Recomputes the HMAC chain with the root key and compares in constant time.</summary>
    public bool HasValidSignature(byte[] rootKey)
    {
        var sig = Hmac(DeriveKey(rootKey), Identifier);
        foreach (var caveat in _caveats)
            sig = Hmac(sig, Encoding.UTF8.GetBytes(caveat));
        return CryptographicOperations.FixedTimeEquals(sig, Signature);
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(2);
        if (Location.Length > 0)
            WriteField(ms, FieldLocation, Encoding.UTF8.GetBytes(Location));
        WriteField(ms, FieldIdentifier, Identifier);
        ms.WriteByte(FieldEos);
        foreach (var caveat in _caveats)
        {
            WriteField(ms, FieldIdentifier, Encoding.UTF8.GetBytes(caveat));
            ms.WriteByte(FieldEos);
        }
        ms.WriteByte(FieldEos);
        WriteField(ms, FieldSignature, Signature);
        return ms.ToArray();
    }

    public string ToBase64() => Convert.ToBase64String(Serialize());

    public static bool TryParseBase64(string value, out Macaroon? macaroon)
    {
        macaroon = null;
        try
        {
            var s = value.Trim().Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return TryDeserialize(Convert.FromBase64String(s), out macaroon);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryDeserialize(byte[] data, out Macaroon? macaroon)
    {
        macaroon = null;
        var r = new Reader(data);
        if (!r.TryReadByte(out var version) || version != 2)
            return false;

        string location = "";
        if (!r.TryReadField(out var type, out var value))
            return false;
        if (type == FieldLocation)
        {
            location = Encoding.UTF8.GetString(value);
            if (!r.TryReadField(out type, out value))
                return false;
        }
        if (type != FieldIdentifier)
            return false;
        var identifier = value;
        if (!r.TryReadByte(out var eos) || eos != FieldEos)
            return false;

        var caveats = new List<string>();
        while (true)
        {
            if (!r.TryPeek(out var next))
                return false;
            if (next == FieldEos)
            {
                r.TryReadByte(out _);
                break;
            }
            if (!r.TryReadField(out type, out value))
                return false;
            if (type == FieldLocation)
            {
                // A location on a caveat only appears on third-party caveats, which we do not support.
                return false;
            }
            if (type != FieldIdentifier)
                return false;
            var caveatId = value;
            if (!r.TryReadByte(out eos))
                return false;
            if (eos != FieldEos)
                return false; // verification id present: third-party caveat
            caveats.Add(Encoding.UTF8.GetString(caveatId));
        }

        if (!r.TryReadField(out type, out value) || type != FieldSignature || value.Length != 32 || !r.AtEnd)
            return false;

        macaroon = new Macaroon(location, identifier, value);
        macaroon._caveats.AddRange(caveats);
        return true;
    }

    static byte[] DeriveKey(byte[] rootKey) => HMACSHA256.HashData(KeyGenerator, rootKey);
    static byte[] Hmac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    static void WriteField(Stream s, byte type, byte[] data)
    {
        s.WriteByte(type);
        WriteUVarint(s, (ulong)data.Length);
        s.Write(data);
    }

    static void WriteUVarint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    sealed class Reader(byte[] data)
    {
        int _pos;
        public bool AtEnd => _pos == data.Length;

        public bool TryPeek(out byte b)
        {
            b = 0;
            if (_pos >= data.Length)
                return false;
            b = data[_pos];
            return true;
        }

        public bool TryReadByte(out byte b)
        {
            if (!TryPeek(out b))
                return false;
            _pos++;
            return true;
        }

        public bool TryReadField(out byte type, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TryReadByte(out type) || type == FieldEos)
                return false;
            ulong len = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (shift > 28 || !TryReadByte(out var b))
                    return false;
                len |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    break;
            }
            if (len > (ulong)(data.Length - _pos))
                return false;
            value = data.AsSpan(_pos, (int)len).ToArray();
            _pos += (int)len;
            return true;
        }
    }
}
