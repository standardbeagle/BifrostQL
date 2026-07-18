using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The four OData v4 system query options this slice honors, parsed out of the request query
    /// string. Parsing enforces the wire-level rules that are independent of the schema: a system
    /// query option may appear at most once (a duplicate is a client error, not a
    /// last-write-wins), the deferred options (<c>$filter</c>/<c>$expand</c>) are cleanly reported
    /// as unimplemented, and any other <c>$</c>-prefixed option is rejected. Non-<c>$</c> custom
    /// query options are ignored, as OData permits. The captured values are still untrusted text —
    /// their schema/number validation happens in <see cref="ODataEntityReadTranslator"/>.
    /// </summary>
    internal sealed record ODataReadOptions(string? Select, string? OrderBy, string? Top, string? Skip)
    {
        /// <summary>
        /// Extracts the supported options from <paramref name="query"/>. Throws
        /// <see cref="ODataProtocolException.BadRequest"/> for a duplicated or unknown system query
        /// option and <see cref="ODataProtocolException.NotImplemented"/> for a recognized but
        /// deferred one, so an unsupported request never silently degrades to a full-table read.
        /// </summary>
        public static ODataReadOptions FromQuery(IQueryCollection query)
        {
            string? select = null, orderBy = null, top = null, skip = null;

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

                    // Deferred to later slices — reported distinctly from an unknown option.
                    case "$filter":
                    case "$expand":
                        throw ODataProtocolException.NotImplemented(
                            $"The '{key}' query option is not implemented.");

                    default:
                        throw ODataProtocolException.BadRequest(
                            $"Unsupported system query option '{key}'.");
                }
            }

            return new ODataReadOptions(select, orderBy, top, skip);
        }

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
