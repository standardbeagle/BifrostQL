using System.Security.Claims;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Pgwire;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the pgwire front
    /// door. Every read is issued as a real SELECT over the ACTUAL pgwire wire (SSL-less
    /// cleartext handshake → simple Query) through <see cref="PgWireLoopback"/>, reaching the
    /// same transformer-pipeline <see cref="IQueryIntentExecutor"/> the base fixture builds —
    /// so tenant isolation, soft-delete, policy read guards and parameterization are proven on
    /// the wire, not shortcut into core.
    ///
    /// <para><b>Read-only.</b> pgwire slices 1-6 implement no write path, so
    /// <see cref="AdapterSupportsMutations"/> stays false and the kit's mutation facts are
    /// skipped by design — an honest reflection of the adapter's surface, not an opt-out.</para>
    ///
    /// <para><b>Sanitized rejections.</b> Per protocol-adapter-security invariant 3 the wire
    /// maps every non-translation fault (tenant-context-required, policy-read-deny) to a single
    /// generic internal_error string; the specific reason is logged server-side, never sent to
    /// the client. So <see cref="ExpectedRejectionFragment"/> is overridden to the sanitized
    /// wire text: the fail-closed facts still prove the read is REJECTED (the wire returns an
    /// ErrorResponse and <see cref="ExecuteReadAsync"/> throws — zero rows delivered), while
    /// honoring the adapter's no-leak contract. The positive facts (tenant sees only its own
    /// rows, tenant/soft-delete WHERE present in the generated SQL, values bind as parameters)
    /// carry the security proof that the pipeline actually ran.</para>
    /// </summary>
    public sealed class PgWireProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        // pgwire is not an HTTP-endpoint adapter: it is driven here on its own loopback front
        // door bound to the fixture's real executor, so nothing is registered on the HTTP
        // endpoint options. The base host still builds the transformer-pipeline executor and
        // the SQL-capture observer this derivation relies on.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        // No write path exists on the pgwire wire; the mutation facts are correctly skipped.
        protected override bool AdapterSupportsMutations => false;

        // The wire withholds the specific rejection reason (invariant 3); the fail-closed
        // facts assert the sanitized text the client actually receives.
        protected override string ExpectedRejectionFragment(string canonicalServerFragment)
            => PgWireProtocol.InternalQueryErrorMessage;

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            var executor = Host.Services.GetRequiredService<IQueryIntentExecutor>();
            var sql = PgWireLoopback.BuildSelect(request.Table, request.Columns, request.Filter);

            // A null principal models "no tenant identity". The wire cannot authenticate
            // "nobody", so it authenticates an identity that simply carries no tenant claim —
            // the tenant transformer then fails closed exactly as the kit intends.
            var principal = request.Principal ?? NoTenantPrincipal();

            var result = await PgWireLoopback.RunAsync(executor, principal, sql, request.Endpoint, Timeout);
            if (result.HasError)
                throw new PgWireQueryException(result.ErrorSqlState!, result.ErrorMessage!);

            return PgWireLoopback.Decode(result);
        }

        /// <summary>An authenticated identity with no tenant claim — the wire equivalent of the kit's null principal.</summary>
        private static ClaimsPrincipal NoTenantPrincipal() =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "user-no-tenant") }, authenticationType: "pgwire"));
    }
}
