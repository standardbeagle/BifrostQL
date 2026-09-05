using System.Diagnostics;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Lists PostgreSQL databases by shelling out to <c>psql</c> — directly for
    /// the OS user the host runs as, or via <c>sudo -u &lt;user&gt;</c> for an
    /// allow-listed account. Used for peer/ident auth where PostgreSQL
    /// authenticates by OS user.
    /// </summary>
    public static class PsqlDatabaseLister
    {
        public static async Task<string[]> ListDatabasesAsync(string connectionString, string? psqlUser, CancellationToken ct)
        {
            // Parse host/port from connection string for psql args
            var kvs = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            kvs.TryGetValue("host", out var host);
            kvs.TryGetValue("port", out var port);

            // Build psql args: output database names only, no headers, no alignment
            var psqlArgs = new List<string> { "-t", "-A", "-c",
                "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname" };
            // For peer auth, only pass -h if it's a socket path (starts with /).
            // Passing -h localhost forces TCP which bypasses peer auth.
            if (!string.IsNullOrWhiteSpace(host) && host.StartsWith('/'))
            {
                psqlArgs.AddRange(new[] { "-h", host });
            }
            if (!string.IsNullOrWhiteSpace(port) && port != "5432")
            {
                psqlArgs.AddRange(new[] { "-p", port });
            }

            var psi = BuildProcessStartInfo(psqlUser);
            foreach (var arg in psqlArgs)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start psql");

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"psql exited with code {proc.ExitCode}" : stderr.Trim();
                throw new InvalidOperationException($"psql failed: {msg}");
            }

            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        /// <summary>
        /// Builds the process invocation for the requested OS user. The caller
        /// picks the OS user psql runs as through sudo — refuse anyone but the
        /// current user or an explicit operator allow-list
        /// (BIFROST_UI_PSQL_PEER_USERS, comma-separated). Without this gate the
        /// desktop API is a privilege-escalation proxy.
        /// </summary>
        internal static ProcessStartInfo BuildProcessStartInfo(string? psqlUser)
        {
            var psi = new ProcessStartInfo();
            // The current user (and no requested user) runs plain psql: peer
            // auth already sees the right OS account, and sudo refuses
            // `-u <self>` without a sudoers rule. sudo is only for allow-listed
            // accounts other than the one the host runs as.
            if (!string.IsNullOrWhiteSpace(psqlUser)
                && !string.Equals(psqlUser, Environment.UserName, StringComparison.Ordinal))
            {
                if (!IsPsqlUserPermitted(psqlUser))
                    throw new InvalidOperationException(
                        "The requested psql OS user is not permitted. Permitted accounts for peer auth: " +
                        string.Join(", ", GetPermittedPsqlUsers()) + ".");
                // Use sudo -u <user> psql for peer auth as a different OS user
                psi.FileName = "sudo";
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(psqlUser);
                psi.ArgumentList.Add("psql");
            }
            else
            {
                psi.FileName = "psql";
            }
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            return psi;
        }

        /// <summary>
        /// The set of OS users the gate permits: the user the host runs as
        /// (always permitted) plus the comma-separated BIFROST_UI_PSQL_PEER_USERS
        /// allow-list, deduped. The connection form offers exactly this set, so
        /// it never submits a user the gate would refuse.
        /// </summary>
        internal static IReadOnlyList<string> GetPermittedPsqlUsers()
        {
            var users = new List<string> { Environment.UserName };
            var allowList = Environment.GetEnvironmentVariable("BIFROST_UI_PSQL_PEER_USERS");
            if (!string.IsNullOrWhiteSpace(allowList))
            {
                foreach (var entry in allowList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!users.Contains(entry, StringComparer.Ordinal))
                        users.Add(entry);
                }
            }
            return users;
        }

        /// <summary>
        /// The current OS user is always permitted; anything else must be named in
        /// the comma-separated BIFROST_UI_PSQL_PEER_USERS environment allow-list.
        /// </summary>
        internal static bool IsPsqlUserPermitted(string psqlUser)
            => GetPermittedPsqlUsers().Contains(psqlUser, StringComparer.Ordinal);
    }
}
