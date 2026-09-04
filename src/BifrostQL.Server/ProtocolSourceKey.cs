using System.Net;

namespace BifrostQL.Server
{
    /// <summary>
    /// The per-source rate-limit key shared by every protocol-adapter front door. It must
    /// identify the CLIENT, not the connection: an <see cref="IPEndPoint"/>'s <c>ToString()</c>
    /// is "ip:port" with an EPHEMERAL port, so keying on the whole endpoint makes a per-source
    /// cap per-connection — a peer opening a fresh connection per attempt evades the cap AND
    /// multiplies the limiter's tracked keys. Key on the IP address alone; a non-IP or absent
    /// endpoint shares one stable sentinel bucket.
    /// </summary>
    internal static class ProtocolSourceKey
    {
        public static string Of(EndPoint? remote) => remote switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            { } other => other.ToString() ?? "unknown",
            null => "unknown",
        };
    }
}
