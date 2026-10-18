using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wf.ControlPlane.Engine.Notifications;

public static class CloudEventSigner
{
    public const string TimestampHeader = "X-Kardia-Timestamp";
    public const string SignatureHeader = "X-Kardia-Signature";

    public static string Sign(ReadOnlySpan<byte> key, string timestamp, ReadOnlySpan<byte> body)
    {
        if (key.Length is < 32 or > 128) throw new ArgumentException("Use a 32-128 byte signing key.", nameof(key));
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 ||
            timestamp != seconds.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Use a canonical nonnegative Unix timestamp.", nameof(timestamp));
        var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
        var message = new byte[checked(prefix.Length + body.Length)];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, message));
    }
}
