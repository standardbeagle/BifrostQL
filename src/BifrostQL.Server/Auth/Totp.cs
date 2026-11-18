using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.Auth
{
    /// <summary>The HMAC a TOTP code is computed with (RFC 6238 section 1.2).</summary>
    public enum LocalTotpAlgorithm
    {
        /// <summary>HMAC-SHA-1: the RFC 6238 default, and the only one every authenticator app reads.</summary>
        Sha1,
        Sha256,
        Sha512,
    }

    /// <summary>
    /// RFC 6238 time-based one-time passwords over RFC 4226 HOTP. Pure functions: the caller owns
    /// the clock, the secret and the replay record. <see cref="Match"/> accepts the current time step
    /// and one step either side (clock skew), compares every candidate in constant time without an
    /// early exit, and returns the step that matched so the caller can refuse any step at or below
    /// the last one it accepted.
    /// </summary>
    internal static class Totp
    {
        /// <summary>Steps accepted either side of the current one.</summary>
        public const int DriftSteps = 1;

        /// <summary>The RFC 6238 time step counter T = floor(unix seconds / period).</summary>
        public static long TimeStep(DateTimeOffset at, int periodSeconds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(periodSeconds, 1);
            return Math.DivRem(at.ToUnixTimeSeconds(), periodSeconds, out var rem) - (rem < 0 ? 1 : 0);
        }

        /// <summary>The <paramref name="digits"/>-digit code for time step <paramref name="step"/> (RFC 4226 section 5.3).</summary>
        public static string Code(ReadOnlySpan<byte> key, long step, LocalTotpAlgorithm algorithm, int digits)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(digits, 6);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 8);
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(counter, step);
            Span<byte> mac = stackalloc byte[64];
            var length = algorithm switch
            {
                LocalTotpAlgorithm.Sha1 => HMACSHA1.HashData(key, counter, mac),
                LocalTotpAlgorithm.Sha256 => HMACSHA256.HashData(key, counter, mac),
                LocalTotpAlgorithm.Sha512 => HMACSHA512.HashData(key, counter, mac),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown TOTP algorithm."),
            };
            var offset = mac[length - 1] & 0x0f;
            var binary = ((mac[offset] & 0x7f) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
            var modulus = 1;
            for (var i = 0; i < digits; i++)
                modulus *= 10;
            return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }

        /// <summary>
        /// The highest step in [<paramref name="currentStep"/> - 1, <paramref name="currentStep"/> + 1]
        /// whose code equals <paramref name="code"/>, or null. All three candidates are computed and
        /// compared with <see cref="CryptographicOperations.FixedTimeEquals"/>; a submitted code of the
        /// wrong length or with a non-digit matches nothing.
        /// </summary>
        public static long? Match(
            ReadOnlySpan<byte> key, string code, long currentStep, LocalTotpAlgorithm algorithm, int digits)
        {
            var wellFormed = code.Length == digits && code.All(char.IsAsciiDigit);
            var submitted = Encoding.ASCII.GetBytes(wellFormed ? code : new string('x', digits));
            long? matched = null;
            for (var step = currentStep - DriftSteps; step <= currentStep + DriftSteps; step++)
            {
                var candidate = Encoding.ASCII.GetBytes(Code(key, step, algorithm, digits));
                if (CryptographicOperations.FixedTimeEquals(candidate, submitted) & wellFormed)
                    matched = step;
            }
            return matched;
        }
    }

    /// <summary>RFC 4648 base32 (upper case, no padding): the secret encoding <c>otpauth://</c> URIs carry.</summary>
    internal static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(ReadOnlySpan<byte> data)
        {
            var output = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0)
                output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return output.ToString();
        }

        public static byte[] Decode(string text)
        {
            var bytes = new List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var c in text)
            {
                var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
                if (value < 0)
                    throw new FormatException("The text is not RFC 4648 base32.");
                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)(buffer >> (bits - 8)));
                    bits -= 8;
                }
            }
            return bytes.ToArray();
        }
    }
}
