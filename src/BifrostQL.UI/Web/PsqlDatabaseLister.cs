using System.Diagnostics;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Lists PostgreSQL databases by shelling out to <c>psql</c> via <c>sudo -u &lt;user&gt;</c>.
    /// Used for peer/ident auth where the .NET process runs as a different OS user
    /// than the one PostgreSQL expects for peer authentication.
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

            var psi = new ProcessStartInfo();
            if (!string.IsNullOrWhiteSpace(psqlUser))
            {
                // The caller picks the OS user psql runs as through sudo — refuse
                // anyone but the current user or an explicit operator allow-list
                // (BIFROST_UI_PSQL_PEER_USERS, comma-separated). Without this gate
                // the desktop API is a privilege-escalation proxy.
                if (!IsPsqlUserPermitted(psqlUser))
                    throw new InvalidOperationException(
                        "The requested psql OS user is not permitted: only the current user " +
                        "or an entry in BIFROST_UI_PSQL_PEER_USERS may be used for peer auth.");
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
            foreach (var arg in psqlArgs)
                psi.ArgumentList.Add(arg);

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

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
        /// The current OS user is always permitted; anything else must be named in
        /// the comma-separated BIFROST_UI_PSQL_PEER_USERS environment allow-list.
        /// </summary>
        internal static bool IsPsqlUserPermitted(string psqlUser)
        {
            if (string.Equals(psqlUser, Environment.UserName, StringComparison.Ordinal))
                return true;
            var allowList = Environment.GetEnvironmentVariable("BIFROST_UI_PSQL_PEER_USERS");
            if (string.IsNullOrWhiteSpace(allowList))
                return false;
            return allowList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(psqlUser, StringComparer.Ordinal);
        }
    }
}
