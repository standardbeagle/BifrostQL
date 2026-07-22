using System.Xml.Linq;
using BifrostQL.Server.Feeds;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Feeds
{
    /// <summary>
    /// The RSS 2.0 and Atom 1.0 writers: structurally valid documents with the required feed/item
    /// fields, and — the security property — every untrusted row value XML-escaped with no
    /// string-spliced markup and no CDATA, so a hostile title/body/link cannot break out of its
    /// element (.claude/rules/protocol-adapter-security.md).
    /// </summary>
    public sealed class FeedWriterTests
    {
        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

        private static FeedDocument Sample(params FeedItem[] items) => new()
        {
            Title = "Example Feed",
            Link = "https://example.test/feed",
            Description = "An example feed",
            FeedId = "https://example.test/feed",
            Updated = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            Items = items,
        };

        private static FeedItem Item(string title = "Item", string? body = "Body", string? link = "https://example.test/1")
            => new()
            {
                Guid = "11111111-2222-3333-4444-555555555555",
                Title = title,
                Body = body,
                Link = link,
                Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            };

        // ---- RSS 2.0 shape ----------------------------------------------------------------------

        [Fact]
        public void Rss_has_the_required_channel_and_item_fields()
        {
            var xml = RssFeedWriter.Write(Sample(Item()));
            var doc = XDocument.Parse(xml); // parses => structurally valid

            var rss = doc.Root!;
            rss.Name.LocalName.Should().Be("rss");
            rss.Attribute("version")!.Value.Should().Be("2.0");

            var channel = rss.Element("channel")!;
            channel.Element("title")!.Value.Should().Be("Example Feed");
            channel.Element("link")!.Value.Should().Be("https://example.test/feed");
            channel.Element("description")!.Value.Should().Be("An example feed");

            var item = channel.Element("item")!;
            item.Element("title")!.Value.Should().Be("Item");
            item.Element("link")!.Value.Should().Be("https://example.test/1");
            item.Element("description")!.Value.Should().Be("Body");
            item.Element("guid")!.Value.Should().Be("11111111-2222-3333-4444-555555555555");
            item.Element("guid")!.Attribute("isPermaLink")!.Value.Should().Be("false");
            item.Element("pubDate").Should().NotBeNull();
        }

        // ---- Atom 1.0 shape ---------------------------------------------------------------------

        [Fact]
        public void Atom_has_the_required_feed_and_entry_fields()
        {
            var xml = AtomFeedWriter.Write(Sample(Item()));
            var doc = XDocument.Parse(xml);

            var feed = doc.Root!;
            feed.Name.Should().Be(Atom + "feed");
            feed.Element(Atom + "id")!.Value.Should().Be("https://example.test/feed");
            feed.Element(Atom + "title")!.Value.Should().Be("Example Feed");
            feed.Element(Atom + "updated").Should().NotBeNull();

            var entry = feed.Element(Atom + "entry")!;
            entry.Element(Atom + "id")!.Value.Should().Be("urn:uuid:11111111-2222-3333-4444-555555555555");
            entry.Element(Atom + "title")!.Value.Should().Be("Item");
            entry.Element(Atom + "updated").Should().NotBeNull();
            entry.Element(Atom + "content")!.Value.Should().Be("Body");
        }

        // ---- hostile data: escaped, never spliced -----------------------------------------------

        private const string Hostile = "</item></channel></rss><script>alert(1)</script>]]>&amp;";

        [Fact]
        public void Rss_escapes_hostile_row_values_and_stays_well_formed()
        {
            var xml = RssFeedWriter.Write(Sample(Item(title: Hostile, body: Hostile, link: Hostile)));

            // Well-formed despite the injection attempt, and the value round-trips as inert TEXT — no
            // injected <script> element, no premature </item> close.
            var doc = XDocument.Parse(xml);
            var item = doc.Root!.Element("channel")!.Element("item")!;
            item.Element("title")!.Value.Should().Be(Hostile);
            item.Element("description")!.Value.Should().Be(Hostile);
            doc.Descendants("script").Should().BeEmpty();
            xml.Should().NotContain("<script>");
            xml.Should().NotContain("<![CDATA[");
        }

        [Fact]
        public void Atom_escapes_hostile_row_values_and_stays_well_formed()
        {
            var xml = AtomFeedWriter.Write(Sample(Item(title: Hostile, body: Hostile, link: Hostile)));

            var doc = XDocument.Parse(xml);
            var entry = doc.Root!.Element(Atom + "entry")!;
            entry.Element(Atom + "title")!.Value.Should().Be(Hostile);
            entry.Element(Atom + "content")!.Value.Should().Be(Hostile);
            doc.Descendants("script").Should().BeEmpty();
            xml.Should().NotContain("<script>");
            xml.Should().NotContain("<![CDATA[");
        }

        // ---- optional fields --------------------------------------------------------------------

        [Fact]
        public void Writers_omit_optional_item_link_and_body_when_absent()
        {
            var document = Sample(Item(body: null, link: null));

            var rss = XDocument.Parse(RssFeedWriter.Write(document));
            var rssItem = rss.Root!.Element("channel")!.Element("item")!;
            rssItem.Element("link").Should().BeNull();
            rssItem.Element("description").Should().BeNull();

            var atom = XDocument.Parse(AtomFeedWriter.Write(document));
            var atomEntry = atom.Root!.Element(Atom + "entry")!;
            atomEntry.Element(Atom + "content").Should().BeNull();
            atomEntry.Elements(Atom + "link").Should().BeEmpty();
        }
    }
}
