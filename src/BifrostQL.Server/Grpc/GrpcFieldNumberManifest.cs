using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.Server.Grpc
{
    /// <summary>One field's stable identity: its wire number and its proto type.</summary>
    public sealed class GrpcFieldEntry
    {
        [JsonPropertyName("number")] public int Number { get; init; }
        /// <summary>
        /// The proto-source type token (see <see cref="GrpcProtoTypeMapper.ProtoToken"/>).
        /// Recorded so an incompatible type change on an existing number — which cannot
        /// change wire type — is detected and <b>fails</b> generation instead of silently
        /// reinterpreting the wire.
        /// </summary>
        [JsonPropertyName("type")] public string Type { get; init; } = "";
    }

    /// <summary>The numbering of one message: live fields plus retired (reserved) numbers/names.</summary>
    public sealed class GrpcMessageNumbering
    {
        [JsonPropertyName("fields")] public Dictionary<string, GrpcFieldEntry> Fields { get; init; } = new();
        [JsonPropertyName("reserved")] public List<int> Reserved { get; init; } = new();
        [JsonPropertyName("reservedNames")] public List<string> ReservedNames { get; init; } = new();
    }

    /// <summary>
    /// The checked-in field-number manifest — the single source of truth for gRPC field
    /// numbers, decoupling "what the schema looks like now" from "what number each column
    /// has always had" (gRPC Schema Contract ADR). Field numbers come from here, never
    /// from a column's ordinal position in the current database read, so the wire contract
    /// survives column reordering, addition, and removal.
    ///
    /// <para><see cref="Reconcile"/> is the enforcement point: it preserves every existing
    /// number, assigns the next free number to a new column, moves a removed column's
    /// number to <c>reserved</c> (never reused), and <b>fails</b> on an incompatible type
    /// change. It is a pure function — it returns a new manifest and never mutates the
    /// input.</para>
    /// </summary>
    public sealed class GrpcFieldNumberManifest
    {
        [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; init; } = 1;
        [JsonPropertyName("messages")] public Dictionary<string, GrpcMessageNumbering> Messages { get; init; } = new();

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static GrpcFieldNumberManifest Empty() => new();

        public static GrpcFieldNumberManifest FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Manifest JSON is empty.", nameof(json));
            return JsonSerializer.Deserialize<GrpcFieldNumberManifest>(json, ReadOptions)
                ?? throw new GrpcSchemaException("gRPC field-number manifest deserialized to null.");
        }

        public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

        /// <summary>
        /// Reconcile this manifest against the currently visible tables/columns and their
        /// mapped proto types, returning a NEW manifest with stable numbering. Drift rules:
        /// additive column → next free number (every existing number preserved); removed
        /// column → number reserved, name reserved-named, never reused; incompatible type
        /// change on an existing number → <see cref="GrpcSchemaException"/>. New columns are
        /// allocated in a deterministic order (sorted by field name) so the result does not
        /// depend on database read order. Messages for tables not currently visible are
        /// preserved untouched, so a table that returns keeps its historical numbers.
        /// </summary>
        public GrpcFieldNumberManifest Reconcile(IEnumerable<GrpcVisibleTable> visibleTables)
        {
            if (visibleTables is null) throw new ArgumentNullException(nameof(visibleTables));

            // Deep-copy every message so the input manifest is never mutated.
            var messages = new Dictionary<string, GrpcMessageNumbering>();
            foreach (var (name, numbering) in Messages)
                messages[name] = CopyNumbering(numbering);

            foreach (var visible in visibleTables)
            {
                var messageName = $"{visible.Table.GraphQlName}Row";
                var numbering = messages.TryGetValue(messageName, out var existing)
                    ? existing
                    : new GrpcMessageNumbering();
                messages[messageName] = numbering;

                var currentColumns = visible.Columns
                    .ToDictionary(
                        c => c.GraphQlName,
                        c => GrpcProtoTypeMapper.ProtoToken(GrpcProtoTypeMapper.Map(c.EffectiveDataType)),
                        StringComparer.Ordinal);

                // Removals: an existing field whose column is gone → reserve its number/name.
                foreach (var goneName in numbering.Fields.Keys
                             .Where(n => !currentColumns.ContainsKey(n))
                             .OrderBy(n => n, StringComparer.Ordinal)
                             .ToList())
                {
                    var goneNumber = numbering.Fields[goneName].Number;
                    numbering.Fields.Remove(goneName);
                    if (!numbering.Reserved.Contains(goneNumber)) numbering.Reserved.Add(goneNumber);
                    if (!numbering.ReservedNames.Contains(goneName)) numbering.ReservedNames.Add(goneName);
                }

                // Type-change check for surviving fields; happens before any allocation so a
                // wire-incompatible change fails generation rather than renumbering.
                foreach (var (fieldName, token) in currentColumns)
                {
                    if (numbering.Fields.TryGetValue(fieldName, out var entry) && entry.Type != token)
                        throw new GrpcSchemaException(
                            $"gRPC schema generation failed: field '{messageName}.{fieldName}' is mapped to " +
                            $"proto type '{token}' but the manifest pins it to '{entry.Type}' at number " +
                            $"{entry.Number}. A field number's wire type cannot change. Reserve number " +
                            $"{entry.Number} and allocate a new number for the changed column.");
                }

                // Additions: allocate deterministically (sorted by name) so read order is irrelevant.
                foreach (var fieldName in currentColumns.Keys
                             .Where(n => !numbering.Fields.ContainsKey(n))
                             .OrderBy(n => n, StringComparer.Ordinal))
                {
                    numbering.Fields[fieldName] = new GrpcFieldEntry
                    {
                        Number = NextFreeNumber(numbering),
                        Type = currentColumns[fieldName],
                    };
                }

                numbering.Reserved.Sort();
                numbering.ReservedNames.Sort(StringComparer.Ordinal);
            }

            return new GrpcFieldNumberManifest { ManifestVersion = ManifestVersion, Messages = messages };
        }

        /// <summary>The number assigned to a live field, or null if the field is unknown/reserved.</summary>
        public int? NumberOf(string messageName, string fieldName) =>
            Messages.TryGetValue(messageName, out var m) && m.Fields.TryGetValue(fieldName, out var e)
                ? e.Number
                : null;

        // Never reuses a live or reserved number: max(used ∪ reserved) + 1.
        private static int NextFreeNumber(GrpcMessageNumbering numbering)
        {
            var max = 0;
            foreach (var e in numbering.Fields.Values) if (e.Number > max) max = e.Number;
            foreach (var r in numbering.Reserved) if (r > max) max = r;
            return max + 1;
        }

        private static GrpcMessageNumbering CopyNumbering(GrpcMessageNumbering src) => new()
        {
            Fields = src.Fields.ToDictionary(
                kv => kv.Key,
                kv => new GrpcFieldEntry { Number = kv.Value.Number, Type = kv.Value.Type },
                StringComparer.Ordinal),
            Reserved = new List<int>(src.Reserved),
            ReservedNames = new List<string>(src.ReservedNames),
        };
    }
}
