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
