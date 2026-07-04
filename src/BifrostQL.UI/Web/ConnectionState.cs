using BifrostQL.Core.Model;
using BifrostQL.Server;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Mutable, shared-by-reference holder for the host's active database
    /// connection. A single instance is created at startup and captured by both
    /// the HTTP endpoints (which mutate it when a connection is activated via
    /// <c>/api/vault/connect</c> or the quickstart self-bind path) and the native
    /// bridge handlers (which read it to run in-process SQL / schema queries).
    ///
    /// Replaces the top-level <c>currentConnectionString</c> / <c>currentProvider</c>
    /// / <c>bifrostOptions</c> / <c>activeVaultPath</c> locals that were previously
    /// captured by every closure in <c>Program.cs</c>.
    /// </summary>
    public sealed class ConnectionState
    {
        /// <summary>The active connection string, or null when nothing is connected.</summary>
        public string? ConnectionString { get; set; }

        /// <summary>The provider detected/selected for <see cref="ConnectionString"/>.</summary>
        public BifrostDbProvider? Provider { get; set; }

        /// <summary>
        /// The BifrostQL setup options captured from <c>AddBifrostQL</c>, used to
        /// rebind the connection string/provider and reset the schema cache when a
        /// new connection is activated at runtime.
        /// </summary>
        public BifrostSetupOptions? Options { get; set; }

        /// <summary>Path to the encrypted vault file (from <c>--vault</c>), or null for the default.</summary>
        public string? VaultPath { get; set; }
    }
}
