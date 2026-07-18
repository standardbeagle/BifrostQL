using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The OData v4 system query options this endpoint honors, parsed out of the request query
    /// string. Parsing enforces the wire-level rules that are independent of the schema: a system
    /// query option may appear at most once (a duplicate is a client error, not a
    /// last-write-wins), the still-deferred option (<c>$expand</c>) is cleanly reported as
    /// unimplemented, and any other <c>$</c>-prefixed option is rejected. Non-<c>$</c> custom
    /// query options are ignored, as OData permits. The captured values are still untrusted text —
    /// their schema/number validation happens in <see cref="ODataEntityReadTranslator"/> and
    /// <see cref="ODataFilterTranslator"/>.
    /// </summary>
    internal sealed record ODataReadOptions(
        string? Select, string? OrderBy, string? Top, string? Skip, string? Filter = null,
        bool Count = false, string? SkipToken = null)
    {
        /// <summary>
        /// Extracts the supported options from <paramref name="query"/>. Throws
        /// <see cref="ODataProtocolException.BadRequest"/> for a duplicated or unknown system query
        /// option and <see cref="ODataProtocolException.NotImplemented"/> for a recognized but
        /// deferred one, so an unsupported request never silently degrades to a full-table read.
        /// <c>$skiptoken</c> (server-driven paging continuation) and <c>$skip</c> are mutually
        /// exclusive — the token is authoritative over the offset, so accepting a client
        /// <c>$skip</c> alongside it would be an ambiguous, un-validated offset source.
        /// </summary>
        public static ODataReadOptions FromQuery(IQueryCollection query)
        {
            string? select = null, orderBy = null, top = null, skip = null, filter = null, skipToken = null;
            bool count = false;

            foreach (var key in query.Keys)
            {
                // Custom query options (not starting with '$') are allowed and ignored by OData.
                if (!key.StartsWith('$'))
                    continue;

                switch (key)
                {
                    case "$select": select = Single(query, key); break;
                    case "$orderby": orderBy = Single(query, key); break;
                    case "$top": top = Single(query, key); break;
                    case "$skip": skip = Single(query, key); break;
                    case "$filter": filter = Single(query, key); break;
                    case "$count": count = ParseBool(Single(query, key)); break;
                    case "$skiptoken": skipToken = Single(query, key); break;

                    // Still deferred — reported distinctly from an unknown option.
                    case "$expand":
                        throw ODataProtocolException.NotImplemented(
                            $"The '{key}' query option is not implemented.");

                    default:
                        throw ODataProtocolException.BadRequest(
                            $"Unsupported system query option '{key}'.");
                }
            }

            if (skipToken is not null && skip is not null)
                throw ODataProtocolException.BadRequest(
                    "The '$skip' and '$skiptoken' query options cannot be combined.");

            return new ODataReadOptions(select, orderBy, top, skip, filter, count, skipToken);
        }

        /// <summary>
        /// Parses the boolean literal <c>$count</c> carries. Only the OData v4 literals
        /// <c>true</c>/<c>false</c> are accepted; anything else is a clean 400 rather than a
        /// silent "treat as false".
        /// </summary>
        private static bool ParseBool(string value) => value.Trim() switch
        {
            "true" => true,
            "false" => false,
            _ => throw ODataProtocolException.BadRequest("$count must be 'true' or 'false'."),
        };

        /// <summary>
        /// Reads a single-valued system query option; a repeated option (<c>?$top=1&amp;$top=2</c>)
        /// is a client error rather than a silently-resolved ambiguity.
        /// </summary>
        private static string Single(IQueryCollection query, string key)
        {
            var values = query[key];
            if (values.Count > 1)
                throw ODataProtocolException.BadRequest(
                    $"The '{key}' query option is specified more than once.");
            return values.ToString();
        }
    }
}
