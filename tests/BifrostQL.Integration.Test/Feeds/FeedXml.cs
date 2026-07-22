using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace BifrostQL.Integration.Test.Feeds
{
    /// <summary>
    /// Feed-document XML helpers built on the .NET XML stack (<see cref="XmlDocument"/> for a strict
    /// well-formedness parse, <see cref="XDocument"/> for structural navigation). A hostile row value that
    /// broke out of its element would make <see cref="Parse"/> throw or would add sibling elements the
    /// structural asserts count — so these are genuine anti-injection assertions, not string matches.
    /// </summary>
    internal static class FeedXml
    {
        public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

        /// <summary>Parses feed XML strictly; a malformed document (injection breakout) throws here.</summary>
        public static XDocument Parse(string xml)
        {
            // A strict DOM load first: any breakout that produced non-well-formed XML faults immediately.
            new XmlDocument().LoadXml(xml);
            return XDocument.Parse(xml);
        }

        public static XElement Channel(XDocument doc) => doc.Root!.Element("channel")!;

        public static IReadOnlyList<XElement> RssItems(XDocument doc)
            => Channel(doc).Elements("item").ToList();

        public static IReadOnlyList<string> RssTitles(XDocument doc)
            => RssItems(doc).Select(i => i.Element("title")!.Value).ToList();

        public static IReadOnlyList<XElement> AtomEntries(XDocument doc)
            => doc.Root!.Elements(Atom + "entry").ToList();

        public static IReadOnlyList<string> AtomTitles(XDocument doc)
            => AtomEntries(doc).Select(e => e.Element(Atom + "title")!.Value).ToList();
    }
}
