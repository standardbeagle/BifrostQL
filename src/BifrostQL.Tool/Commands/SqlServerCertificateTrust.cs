namespace BifrostQL.Tool.Commands;

/// <summary>
/// Assembly of SQL Server connection strings from CLI arguments, and the certificate-trust
/// waiver that goes with them.
///
/// SqlClient 6.x encrypts by default (<c>Encrypt=Mandatory</c>), but encryption alone only
/// buys confidentiality. <c>TrustServerCertificate=True</c> switches off the other half —
/// the server's identity is no longer verified, so anyone able to sit on the network path
/// terminates the TLS session themselves and reads the credentials plus all query traffic.
/// It is therefore never a default here: it is an explicit per-invocation opt-in
/// (<c>--trust-server-certificate</c>), stated by the operator who already trusts a
/// specific self-signed or internally issued certificate.
/// </summary>
public static class SqlServerCertificateTrust
{
    /// <summary>The CLI flag that waives certificate validation.</summary>
    public const string Flag = "--trust-server-certificate";

    /// <summary>
    /// What to tell a user whose connect failed on certificate validation. Both routes are
    /// named because the connection string reaching a failure may have come from either the
    /// positional shorthand or an explicit <c>--connection-string</c>, and a remedy that
    /// only covers one of them strands the other.
    /// </summary>
    public const string CertificateRemedy =
        "The server's TLS certificate could not be validated. If you trust this server and its "
        + "certificate is self-signed or internally issued, re-run with " + Flag
        + " (or add TrustServerCertificate=True to your connection string). That accepts any "
        + "certificate the server presents, so the connection is encrypted but the server's "
        + "identity is unverified — only do it on a network path you trust.";

    /// <summary>The warning shown when an invocation is running with the waiver in force.</summary>
    public static string WaiverWarning(string server) =>
        $"Warning: connecting to {server} WITHOUT validating its TLS certificate "
        + "(--trust-server-certificate). The connection is encrypted but the server's identity "
        + "is unverified and interceptable on the network path.";

    /// <summary>
    /// Build a SQL Server connection string from the positional <c>&lt;server&gt;
    /// &lt;database&gt;</c> arguments. A null/blank <paramref name="user"/> selects
    /// integrated authentication. <c>TrustServerCertificate=True</c> is emitted only when
    /// <paramref name="trustServerCertificate"/> was explicitly opted into.
    /// </summary>
    public static string Build(string server, string database, string? user, string? password, bool trustServerCertificate)
    {
        var parts = new List<string>
        {
            $"Server={server}",
            $"Database={database}",
        };

        if (string.IsNullOrWhiteSpace(user))
            parts.Add("Trusted_Connection=True");
        else
            parts.AddRange([$"User Id={user}", $"Password={password}"]);

        if (trustServerCertificate)
            parts.Add("TrustServerCertificate=True");

        return string.Join(';', parts);
    }

    /// <summary>
    /// Whether a connect failure was the TLS certificate being rejected. Drivers surface this
    /// as a nested SSL/authentication exception with the detail several levels down, so the
    /// whole chain is inspected. Matching on message text is inexact by nature — this only
    /// decides whether to append a hint, never whether the connect succeeded.
    /// </summary>
    public static bool IsCertificateValidationFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("cert chain", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
