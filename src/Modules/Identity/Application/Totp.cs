using System.Security.Cryptography;

namespace Zaagl.Modules.Identity.Application;

/// <summary>
/// RFC 6238 TOTP, hand-rolled per spec §2 ("No third-party library for the scramble" sets the tone for
/// this build; a TOTP implementation is ~30 lines and not worth a dependency). 30-second step, 6 digits,
/// HMAC-SHA1 (the universal choice every authenticator app - Google Authenticator, Authy, 1Password -
/// expects; HMAC-SHA256 would be stronger but incompatible with real apps).
/// </summary>
public static class Totp
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    public static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20); // 160 bits, the RFC's recommended size
        return ToBase32(bytes);
    }

    public static string OtpAuthUri(string secret, string accountEmail, string issuer) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountEmail)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}&algorithm=SHA1";

    /// <summary>Accepts the current step and one step on either side, to tolerate clock drift.</summary>
    public static bool Validate(string secret, string code, DateTimeOffset now)
    {
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return false;
        var key = FromBase32(secret);
        var counter = now.ToUnixTimeSeconds() / StepSeconds;
        for (var delta = -1; delta <= 1; delta++)
            if (ComputeCode(key, counter + delta) == code) return true;
        return false;
    }

    /// <summary>Internal (not private) so tests can generate a valid current code without reflection.</summary>
    internal static string ComputeCode(byte[] key, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counterBytes, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var code = binary % 1_000_000;
        return code.ToString("D6");
    }

    private static string ToBase32(byte[] data)
    {
        var result = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) result.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return result.ToString();
    }

    internal static byte[] FromBase32(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var index = Array.IndexOf(Base32Alphabet, c);
            if (index < 0) continue;
            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
