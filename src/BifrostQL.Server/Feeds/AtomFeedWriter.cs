using System.Globalization;
using System.Text;
using System.Xml;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// Serializes a <see cref="FeedDocument"/> to an Atom 1.0 document. As with the RSS writer, every
    /// value goes through <see cref="XmlWriter"/> escaping and no CDATA is used, so hostile row values
    /// are inert escaped text. Entry <c>content</c> is written with <c>type="html"</c> but still
    /// escaped — the reader unescapes it; it is never emitted as raw child markup
    /// (.claude/rules/protocol-adapter-security.md).
    /// </summary>
    public static class AtomFeedWriter
    {
        private const string AtomNamespace = "http://www.w3.org/2005/Atom";

        public static string Write(FeedDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            using var buffer = new MemoryStream();
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
            };

            using (var writer = XmlWriter.Create(buffer, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("feed", AtomNamespace);

                // Required Atom feed fields: id, title, updated.
                writer.WriteElementString("id", AtomNamespace, document.FeedId);
                writer.WriteElementString("title", AtomNamespace, document.Title);
                writer.WriteElementString("updated", AtomNamespace, Rfc3339(document.Updated ?? DateTime.UtcNow));
                WriteLink(writer, "self", document.Link);

                foreach (var item in document.Items)
                {
                    writer.WriteStartElement("entry", AtomNamespace);
                    writer.WriteElementString("id", AtomNamespace, $"urn:uuid:{item.Guid}");
                    writer.WriteElementString("title", AtomNamespace, item.Title);
                    writer.WriteElementString("updated", AtomNamespace, Rfc3339(item.Timestamp));
                    if (item.Link is not null)
                        WriteLink(writer, "alternate", item.Link);
                    if (item.Body is not null)
                    {
                        writer.WriteStartElement("content", AtomNamespace);
                        writer.WriteAttributeString("type", "html");
                        writer.WriteString(item.Body);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); // entry
                }

                writer.WriteEndElement(); // feed
                writer.WriteEndDocument();
            }

            return new UTF8Encoding(false).GetString(buffer.ToArray());
        }

        private static void WriteLink(XmlWriter writer, string rel, string href)
        {
            writer.WriteStartElement("link", AtomNamespace);
            writer.WriteAttributeString("rel", rel);
            writer.WriteAttributeString("href", href);
            writer.WriteEndElement();
        }

        // Atom timestamps are RFC 3339; a UTC instant renders with a 'Z' offset.
        private static string Rfc3339(DateTime utc)
            => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
