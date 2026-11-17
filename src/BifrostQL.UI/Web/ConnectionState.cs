using BifrostQL.Core.Model;
using BifrostQL.Server;

namespace BifrostQL.UI.Web
{
    /// <summary>The bound database: connection string and provider, always swapped together.</summary>
    public sealed record ActiveConnection(string ConnectionString, BifrostDbProvider Provider);

    /// <summary>
    /// Shared-by-reference holder for the host's active database connection. A single
    /// instance is created at startup and captured by the HTTP endpoints (which activate
    /// a connection via <c>/api/vault/connect</c> or the quickstart self-bind) and the native bridge handlers (which read it to run
    /// in-process SQL / schema queries).
    ///
    /// The connection string and provider live in one immutable <see cref="ActiveConnection"/>
    /// swapped atomically, so a reader takes one snapshot and can never pair a new
    /// connection string with the old provider. Activations are serialized: each one
    /// rebinds the BifrostQL options, the profile set and the schema cache as one step, so
    /// two concurrent connects cannot leave the snapshot and the options pointing at
    /// different databases.
    /// </summary>
    public sealed class ConnectionState
    {
        private ActiveConnection? _active;
        private readonly SemaphoreSlim _activation = new(1, 1);

        /// <summary>The active connection, or null when nothing is connected. Read once per use.</summary>
        public ActiveConnection? Active => Volatile.Read(ref _active);

        /// <summary>
        /// The BifrostQL setup options captured from <c>AddBifrostQL</c>, used to
        /// rebind the connection string/provider and reset the schema cache when a
        /// new connection is activated at runtime.
        /// </summary>
        public BifrostSetupOptions? Options { get; set; }

        /// <summary>Path to the encrypted vault file (from <c>--vault</c>), or null for the default.</summary>
        public string? VaultPath { get; set; }

        /// <summary>The active connection, or the bridge's "connect first" error.</summary>
        public ActiveConnection RequireActive() =>
            Active ?? throw new InvalidOperationException("No active database connection. Connect to a database first.");

        /// <summary>
        /// Binds the startup connection (the <c>bifrostui "&lt;connection&gt;"</c> argument) before
        /// the host builds its options, so there is nothing to rebind yet.
        /// </summary>
        public void SetInitial(string connectionString, BifrostDbProvider provider) =>
            Volatile.Write(ref _active, new ActiveConnection(connectionString, provider));

        /// <summary>
        /// Makes <paramref name="connection"/> the active database: swaps the snapshot, rebinds
        /// the BifrostQL options, rebinds profiles from <paramref name="profileSchema"/>'s bundled
        /// config (null clears them) and resets the schema cache.
        /// </summary>
        public async Task ActivateAsync(IServiceProvider services, ActiveConnection connection, string? profileSchema, CancellationToken ct)
        {
            await _activation.WaitAsync(ct);
            try
            {
                Volatile.Write(ref _active, connection);
                Options?.BindConnectionString(connection.ConnectionString);
                Options?.BindProvider(connection.Provider.ToString().ToLowerInvariant());
                // Rebind profiles BEFORE ResetSchema, so the next schema rebuild picks up
                // the new profile set.
                await ProfileActivation.RebindProfilesAsync(services, profileSchema);
                Options?.ResetSchema(services);
            }
            finally
            {
                _activation.Release();
            }
        }
    }
}
