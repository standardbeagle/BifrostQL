using System.Text;
using System.Text.Json;
using System.Xml;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Serializes the identity-filtered <see cref="ODataEntity"/> projection into the two OData v4
    /// discovery documents: the service document (OData JSON) and the <c>$metadata</c> CSDL/EDMX
    /// (XML). Both are built ONLY from schema-derived model names — no request-provided identifier
    /// is ever interpolated — and the EDMX is written through <see cref="XmlWriter"/>, so every
    /// name is XML-escaped structurally rather than by hand
    /// (.claude/rules/protocol-adapter-security.md invariants 2/4).
    /// </summary>
    internal static class ODataDocumentWriter
    {
        /// <summary>The single EDM schema namespace all entity types live under.</summary>
        public const string SchemaNamespace = "BifrostQL";

        /// <summary>
        /// Value serialization for entity-collection payloads: web defaults render each CLR value as
        /// its natural JSON type — numbers as numbers, booleans as booleans, <c>null</c> as JSON
        /// null, <see cref="DateTime"/>/<see cref="DateTimeOffset"/> as ISO-8601 strings, byte arrays
        /// as base64 — matching the coercions the model already applies to a resolved row.
        /// </summary>
        private static readonly JsonSerializerOptions ValueJsonOptions = new(JsonSerializerDefaults.Web);
        private const string ContainerName = "Container";
        private const string EdmxNs = "http://docs.oasis-open.org/odata/ns/edmx";
        private const string EdmNs = "http://docs.oasis-open.org/odata/ns/edm";

        /// <summary>
        /// The OData JSON service document: one <c>EntitySet</c> entry per visible entity, plus
        /// the <c>@odata.context</c> pointer to <c>$metadata</c>. <paramref name="serviceRoot"/>
        /// is the request's base URI (scheme + host + the OData route prefix), with no trailing
        /// slash.
        /// </summary>
        public static string WriteServiceDocument(IReadOnlyList<ODataEntity> entities, string serviceRoot)
        {
            if (entities is null) throw new ArgumentNullException(nameof(entities));

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("@odata.context", $"{serviceRoot}/$metadata");
                writer.WriteStartArray("value");
                foreach (var entity in entities)
                {
                    var name = entity.Table.GraphQlName;
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteString("kind", "EntitySet");
                    writer.WriteString("url", name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>
        /// The CSDL/EDMX <c>$metadata</c> document: an <c>EntityType</c> per visible entity (its
        /// key, its readable columns typed to <c>Edm.*</c> primitives, and its navigation
        /// properties to other visible entities) plus an <c>EntityContainer</c> exposing each as
        /// an <c>EntitySet</c>. <paramref name="typeMapper"/> is the model's dialect-aware mapper.
        /// </summary>
        public static string WriteMetadata(IReadOnlyList<ODataEntity> entities, ITypeMapper typeMapper)
        {
            if (entities is null) throw new ArgumentNullException(nameof(entities));
            if (typeMapper is null) throw new ArgumentNullException(nameof(typeMapper));

            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };

            using var buffer = new MemoryStream();
            using (var writer = XmlWriter.Create(buffer, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("edmx", "Edmx", EdmxNs);
                writer.WriteAttributeString("Version", "4.0");

                writer.WriteStartElement("edmx", "DataServices", EdmxNs);
                writer.WriteStartElement("Schema", EdmNs);
                writer.WriteAttributeString("Namespace", SchemaNamespace);

                foreach (var entity in entities)
                    WriteEntityType(writer, entity, typeMapper);

                WriteEntityContainer(writer, entities);

                writer.WriteEndElement(); // Schema
                writer.WriteEndElement(); // DataServices
                writer.WriteEndElement(); // Edmx
                writer.WriteEndDocument();
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>
        /// Serializes an entity-set read result as the OData v4 collection response: the
        /// <c>@odata.context</c> annotation followed by a <c>value</c> array of entity objects. Each
        /// object exposes only <paramref name="columns"/> (the projected/visible set), keyed by EDM
        /// property name (the column's GraphQL name) and typed via <see cref="ValueJsonOptions"/>. A
        /// row is read positionally by database column name from the resolved dictionary, so a value
        /// the pipeline masked/omitted surfaces as JSON null rather than leaking. When
        /// <paramref name="projected"/> is set (a <c>$select</c> was applied) the context annotation
        /// carries the projection list, per the OData v4 context-URL rules. <paramref name="count"/>
        /// (when a <c>$count=true</c> was requested) is emitted as <c>@odata.count</c> — the
        /// pipeline-filtered total computed through the same intent as the returned rows;
        /// <paramref name="nextLink"/> (when the page was bounded and more rows remain) is emitted
        /// as the <c>@odata.nextLink</c> whose <c>$skiptoken</c> is opaque and integrity-protected.
        /// </summary>
        public static string WriteEntityCollection(
            string serviceRoot,
            string entitySetName,
            IReadOnlyList<ColumnDto> columns,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            bool projected,
            long? count = null,
            string? nextLink = null)
        {
            if (columns is null) throw new ArgumentNullException(nameof(columns));
            if (rows is null) throw new ArgumentNullException(nameof(rows));

            var context = projected
                ? $"{serviceRoot}/$metadata#{entitySetName}({string.Join(",", columns.Select(c => c.GraphQlName))})"
                : $"{serviceRoot}/$metadata#{entitySetName}";

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("@odata.context", context);
                if (count is not null)
                    writer.WriteNumber("@odata.count", count.Value);
                writer.WriteStartArray("value");
                foreach (var row in rows)
                {
                    writer.WriteStartObject();
                    foreach (var column in columns)
                    {
                        writer.WritePropertyName(column.GraphQlName);
                        var value = row.TryGetValue(column.DbName, out var v) ? v : null;
                        JsonSerializer.Serialize(writer, value, ValueJsonOptions);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (nextLink is not null)
                    writer.WriteString("@odata.nextLink", nextLink);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static void WriteEntityType(XmlWriter writer, ODataEntity entity, ITypeMapper typeMapper)
        {
            writer.WriteStartElement("EntityType", EdmNs);
            writer.WriteAttributeString("Name", entity.Table.GraphQlName);

            // Composite keys are emitted in full — every key column, in order, gets a PropertyRef.
            if (entity.KeyColumns.Count > 0)
            {
                writer.WriteStartElement("Key", EdmNs);
                foreach (var key in entity.KeyColumns)
                {
                    writer.WriteStartElement("PropertyRef", EdmNs);
                    writer.WriteAttributeString("Name", key.GraphQlName);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // Key
            }

            foreach (var column in entity.Columns)
            {
                writer.WriteStartElement("Property", EdmNs);
                writer.WriteAttributeString("Name", column.GraphQlName);
                writer.WriteAttributeString("Type", ODataEdmTypes.ForColumn(column, typeMapper));
                writer.WriteAttributeString("Nullable", column.IsNullable ? "true" : "false");
                writer.WriteEndElement(); // Property
            }

            foreach (var nav in entity.Navigations)
            {
                writer.WriteStartElement("NavigationProperty", EdmNs);
                writer.WriteAttributeString("Name", nav.Name);
                var typeRef = $"{SchemaNamespace}.{nav.TargetEntity}";
                writer.WriteAttributeString("Type", nav.IsCollection ? $"Collection({typeRef})" : typeRef);
                writer.WriteEndElement(); // NavigationProperty
            }

            writer.WriteEndElement(); // EntityType
        }

        private static void WriteEntityContainer(XmlWriter writer, IReadOnlyList<ODataEntity> entities)
        {
            writer.WriteStartElement("EntityContainer", EdmNs);
            writer.WriteAttributeString("Name", ContainerName);

            foreach (var entity in entities)
            {
                writer.WriteStartElement("EntitySet", EdmNs);
                writer.WriteAttributeString("Name", entity.Table.GraphQlName);
                writer.WriteAttributeString("EntityType", $"{SchemaNamespace}.{entity.Table.GraphQlName}");
                writer.WriteEndElement(); // EntitySet
            }

            writer.WriteEndElement(); // EntityContainer
        }
    }
}
