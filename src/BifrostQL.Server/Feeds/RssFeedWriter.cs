using System.Globalization;
using System.Text;
using System.Xml;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// Serializes a <see cref="FeedDocument"/> to an RSS 2.0 document. Every value is written through
    /// <see cref="XmlWriter"/>, which escapes <c>&lt; &gt; &amp; "</c> unconditionally — there is no
    /// string-spliced markup and no CDATA anywhere, so a hostile row value (e.g. a title containing
    /// <c>&lt;/item&gt;&lt;script&gt;</c> or a body containing <c>]]&gt;</c>) is emitted as inert
    /// escaped text and can never break out of its element (.claude/rules/protocol-adapter-security.md).
    /// </summary>
    public static class RssFeedWriter
    {
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
                writer.WriteStartElement("rss");
                writer.WriteAttributeString("version", "2.0");
                writer.WriteStartElement("channel");

                // Required RSS 2.0 channel fields.
                writer.WriteElementString("title", document.Title);
                writer.WriteElementString("link", document.Link);
                writer.WriteElementString("description", document.Description ?? document.Title);
                if (document.Updated is { } updated)
                    writer.WriteElementString("lastBuildDate", Rfc1123(updated));

                foreach (var item in document.Items)
                {
                    writer.WriteStartElement("item");
                    writer.WriteElementString("title", item.Title);
                    if (item.Link is not null)
                        writer.WriteElementString("link", item.Link);
                    if (item.Body is not null)
                        writer.WriteElementString("description", item.Body);
                    // A non-URL, globally-unique id: isPermaLink="false" tells readers not to treat
                    // the guid as a URL.
                    writer.WriteStartElement("guid");
                    writer.WriteAttributeString("isPermaLink", "false");
                    writer.WriteString(item.Guid);
                    writer.WriteEndElement();
                    writer.WriteElementString("pubDate", Rfc1123(item.Timestamp));
                    writer.WriteEndElement(); // item
                }

                writer.WriteEndElement(); // channel
                writer.WriteEndElement(); // rss
                writer.WriteEndDocument();
            }

            return new UTF8Encoding(false).GetString(buffer.ToArray());
        }

        // RSS 2.0 dates are RFC 822 / RFC 1123; "r" formats UTC as a GMT string.
        private static string Rfc1123(DateTime utc)
            => utc.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
    }
}
