using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// Builds the deterministic S3-style XML error envelope. Deliberately minimal: only
    /// &lt;Code&gt;, &lt;Message&gt;, and &lt;RequestId&gt; — no &lt;Resource&gt; (the request
    /// path), no canonical-request/signature detail, and no tenant data ever go on the wire,
    /// per .claude/rules/protocol-adapter-security.md invariant 3. The message text itself
    /// comes only from <see cref="S3ProtocolException"/>'s fixed, curated strings.
    /// </summary>
    public static class S3XmlError
    {
        /// <summary>Generates a fresh, non-guessable per-request id for the error envelope.</summary>
        public static string NewRequestId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        public static string Write(string code, string message, string requestId)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<Error>");
            sb.Append("<Code>").Append(Escape(code)).Append("</Code>");
            sb.Append("<Message>").Append(Escape(message)).Append("</Message>");
            sb.Append("<RequestId>").Append(Escape(requestId)).Append("</RequestId>");
            sb.Append("</Error>");
            return sb.ToString();
        }

        private static string Escape(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
