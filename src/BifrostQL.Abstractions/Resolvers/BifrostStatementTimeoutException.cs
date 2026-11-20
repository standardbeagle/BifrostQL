using System;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// A statement the database cancelled at its own time limit (PostgreSQL's
    /// <c>statement_timeout</c>, SQLSTATE <c>57014</c>; a driver command timeout) while the
    /// request was still live. The GraphQL HTTP middleware throws it to the host in place of a
    /// response, after the statement's transaction has rolled back and before anything is written,
    /// so a middleware ahead of BifrostQL can answer with its own status (a 503, say). A request
    /// cancelled by its own token is the client leaving and never raises it. The driver's error is
    /// the <see cref="Exception.InnerException"/>; its text is for the server log only.
    /// </summary>
    public sealed class BifrostStatementTimeoutException : Exception
    {
        public BifrostStatementTimeoutException(Exception innerException)
            : base("The database cancelled a statement at its time limit.", innerException)
        {
        }
    }
}
