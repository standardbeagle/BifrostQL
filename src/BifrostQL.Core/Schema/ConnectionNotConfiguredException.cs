using System;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Thrown by a <see cref="PathCache{T}"/> endpoint loader when the host is running in
    /// deferred-connection mode (started without a connection string, e.g. the desktop UI
    /// before the user connects to a database). Derives from
    /// <see cref="InvalidOperationException"/> so request-path callers that fail fast on the
    /// broader type are unaffected; background pollers (e.g. the CDC outbox dispatcher)
    /// catch this type specifically and idle quietly until a connection is configured and
    /// the cache is reset.
    /// </summary>
    public sealed class ConnectionNotConfiguredException : InvalidOperationException
    {
        public ConnectionNotConfiguredException()
            : base("Connection string has not been configured.")
        {
        }
    }
}
