using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Message-level codec tests: <see cref="LdapMessageReader"/> decodes every request kind the
    /// slice understands (Bind, Search, Unbind, Abandon, Extended) and the controls envelope, while
    /// <see cref="LdapMessageWriter"/> encodes the responses (Bind/SearchDone/Extended, plus the
    /// RootDSE and subschema SearchResultEntries) back to bytes that re-decode.
    ///
    /// <para>The load-bearing security facts live here: the nesting-depth cap rejects a filter one
    /// level past the cap (and accepts one exactly at it), the filter-component and attribute caps
    /// reject an over-count, and an oversized definite length is refused before allocation. These
    /// are the pre-auth DoS guards of <c>protocol-adapter-security</c> invariants 5 and 6.</para>
    /// </summary>
    public sealed class LdapMessageCodecTests
    {
        private static LdapMessageReader Reader(int depth = 32, int filterComponents = 1024, int attributes = 1024, int maxMessage = 1 << 20)
            => new(maxMessage, depth, filterComponents, attributes);

        private static async Task<LdapRequest> DecodeAsync(byte[] message, LdapMessageReader? reader = null)
        {
            var request = await (reader ?? Reader()).ReadRequestAsync(new MemoryStream(message), default);
            request.Should().NotBeNull("a complete message must decode to a request");
            return request!;
        }

        // ---- request decode ----

        [Fact]
        public async Task BindRequest_Decodes_VersionNameAndAuthKind()
        {
            var message = LdapWire.Message(1, LdapWire.BindRequest(version: 3, name: "cn=admin", password: "secret"));

            var request = await DecodeAsync(message);

            request.MessageId.Should().Be(1);
            var bind = request.Operation.Should().BeOfType<LdapBindRequest>().Subject;
            bind.Version.Should().Be(3);
            bind.Name.Should().Be("cn=admin");
            bind.AuthKind.Should().Be(LdapBindAuthKind.Simple);
        }

        [Fact]
        public async Task SearchRequest_Decodes_BaseAndPresentFilter()
        {
            var message = LdapWire.Message(2, LdapWire.SearchRequest(
                baseObject: "dc=example,dc=com", filter: LdapWire.FilterPresent("objectClass"),
                attributes: new[] { "cn", "mail" }));

            var request = await DecodeAsync(message);

            var search = request.Operation.Should().BeOfType<LdapSearchRequest>().Subject;
            search.BaseObject.Should().Be("dc=example,dc=com");
            search.Filter.Should().BeOfType<LdapFilter.Present>().Which.Attribute.Should().Be("objectClass");
            search.Attributes.Should().Equal("cn", "mail");
        }

        [Fact]
        public async Task UnbindRequest_Decodes()
        {
            var request = await DecodeAsync(LdapWire.Message(3, LdapWire.UnbindRequest()));
            request.Operation.Should().BeOfType<LdapUnbindRequest>();
        }

        [Fact]
        public async Task AbandonRequest_Decodes_TargetMessageId()
        {
            var request = await DecodeAsync(LdapWire.Message(4, LdapWire.AbandonRequest(2)));
            request.Operation.Should().BeOfType<LdapAbandonRequest>().Which.TargetMessageId.Should().Be(2);
        }

        [Fact]
        public async Task ExtendedRequest_Decodes_RequestName()
        {
            var request = await DecodeAsync(LdapWire.Message(5, LdapWire.ExtendedRequest(LdapProtocol.StartTlsOid)));
            request.Operation.Should().BeOfType<LdapExtendedRequest>().Which.RequestName.Should().Be(LdapProtocol.StartTlsOid);
        }

        [Fact]
        public async Task UnknownProtocolOp_DecodesAsUnknown_CarryingItsTag()
        {
            // Arrange: a ModifyRequest [APPLICATION 6] (0x66) — a real op the codec does not model.
            const byte modifyRequest = 0x66;
            var message = LdapWire.Message(6, BerWriter.Tlv(modifyRequest, BerWriter.OctetString("cn=x")));

            var request = await DecodeAsync(message);

            request.Operation.Should().BeOfType<LdapUnknownOperation>().Which.Tag.Should().Be(modifyRequest);
        }

        [Fact]
        public async Task Controls_Envelope_Decodes_OidAndCriticality()
        {
            var controls = LdapWire.Controls(LdapWire.Control("1.2.840.113556.1.4.319", criticality: true));
            var message = LdapWire.Message(7, LdapWire.SearchRequest(), controls);

            var request = await DecodeAsync(message);

            request.Controls.Should().ContainSingle();
            request.Controls[0].Oid.Should().Be("1.2.840.113556.1.4.319");
            request.Controls[0].Criticality.Should().BeTrue();
        }

        [Fact]
        public async Task NegativeMessageId_IsRejected()
        {
            // Arrange: message ID -1 is illegal; the decoder must refuse it, not wrap it.
            var message = BerWriter.Sequence(BerWriter.Integer(-1), LdapWire.UnbindRequest());

            var act = async () => await Reader().ReadRequestAsync(new MemoryStream(message), default);

            await act.Should().ThrowAsync<LdapProtocolException>();
        }

        // ---- caps (the DoS guards) ----

        [Fact]
        public async Task FilterNesting_ExceedingDepthCap_IsRejected_NoStackOverflow()
        {
            // Arrange: cap+1 nested 'not' connectives around a present leaf. With the whole message
            // buffered, the filter parse recurses synchronously; an unguarded decoder would grow the
            // physical stack one frame per level → uncatchable StackOverflowException tearing down the
            // host. The depth guard must turn this into a clean, catchable protocol error. (Removing
            // the guard in DecodeFilter/NextDepth makes this parse successfully — the revert-proof.)
            const int cap = 8;
            var message = LdapWire.Message(1, LdapWire.SearchRequest(filter: LdapWire.NestedNotFilter(cap + 1)));

            var act = async () => await Reader(depth: cap).ReadRequestAsync(new MemoryStream(message), default);

            await act.Should().ThrowAsync<LdapProtocolException>().WithMessage("*nesting*");
        }

        [Fact]
        public async Task FilterNesting_UpToDepthCap_StillParses()
        {
            // Arrange: exactly `cap` nested 'not' connectives — the deepest input the guard must still
            // accept (the cap is headroom, not a false trip).
            const int cap = 8;
            var message = LdapWire.Message(1, LdapWire.SearchRequest(filter: LdapWire.NestedNotFilter(cap)));

            var request = await Reader(depth: cap).ReadRequestAsync(new MemoryStream(message), default);

            var filter = request!.Operation.Should().BeOfType<LdapSearchRequest>().Subject.Filter;
            for (var i = 0; i < cap; i++)
                filter = filter.Should().BeOfType<LdapFilter.Not>().Subject.Child;
            filter.Should().BeOfType<LdapFilter.Present>();
        }

        [Fact]
        public async Task FilterComponentCount_ExceedingCap_IsRejected()
        {
            // Arrange: an 'and' of (cap) present leaves + the 'and' node itself exceeds the component cap.
            const int cap = 4;
            var leaves = Enumerable.Range(0, cap).Select(_ => LdapWire.FilterPresent("cn")).ToArray();
            var message = LdapWire.Message(1, LdapWire.SearchRequest(filter: LdapWire.FilterAnd(leaves)));

            var act = async () => await Reader(filterComponents: cap).ReadRequestAsync(new MemoryStream(message), default);

            await act.Should().ThrowAsync<LdapProtocolException>().WithMessage("*component*");
        }

        [Fact]
        public async Task SearchAttributeCount_ExceedingCap_IsRejected()
        {
            const int cap = 3;
            var attributes = Enumerable.Range(0, cap + 1).Select(i => $"attr{i}").ToArray();
            var message = LdapWire.Message(1, LdapWire.SearchRequest(attributes: attributes));

            var act = async () => await Reader(attributes: cap).ReadRequestAsync(new MemoryStream(message), default);

            await act.Should().ThrowAsync<LdapProtocolException>().WithMessage("*attribute*");
        }

        [Fact]
        public async Task MessageLength_BeyondCap_IsRejected_BeforeBodyAllocation()
        {
            // Arrange: a legitimate bind message, but a reader whose message cap is far below its size.
            // The oversized declared length must be refused reading the header, before the body buffer.
            var message = LdapWire.Message(1, LdapWire.BindRequest(name: "cn=admin", password: "secret"));

            var act = async () => await Reader(maxMessage: 8).ReadRequestAsync(new MemoryStream(message), default);

            await act.Should().ThrowAsync<LdapProtocolException>().WithMessage("*exceeds*");
        }

        [Fact]
        public async Task TruncatedBody_AtEof_IsProtocolError_NotCleanClose()
        {
            // Arrange: the header declares a body longer than the bytes that follow.
            var full = LdapWire.Message(1, LdapWire.UnbindRequest());
            var truncated = full[..^1]; // drop the last body byte

            var act = async () => await Reader().ReadRequestAsync(new MemoryStream(truncated), default);

            await act.Should().ThrowAsync<LdapProtocolException>();
        }

        [Fact]
        public async Task EmptyStream_BetweenMessages_IsCleanClose()
        {
            // A peer that closes cleanly between messages yields a null request, not an exception.
            var request = await Reader().ReadRequestAsync(new MemoryStream(Array.Empty<byte>()), default);
            request.Should().BeNull();
        }

        // ---- response encode ----

        [Theory]
        [InlineData(0)]   // success
        [InlineData(53)]  // unwillingToPerform
        [InlineData(2)]   // protocolError
        public void BindResponse_Encodes_AndReDecodes(int codeValue)
        {
            var code = (LdapResultCode)codeValue;
            var bytes = LdapMessageWriter.BindResponse(9, code, "diagnostic");

            var response = LdapWire.ParseResponse(bytes);

            response.MessageId.Should().Be(9);
            response.OpTag.Should().Be(LdapProtocol.BindResponse);
            response.ResultCode.Should().Be(code);
        }

        [Fact]
        public void ExtendedResponse_WithResponseName_Encodes_AndReDecodes()
        {
            var bytes = LdapMessageWriter.ExtendedResponse(11, LdapResultCode.UnwillingToPerform, "no", LdapProtocol.StartTlsOid);

            var response = LdapWire.ParseResponse(bytes);

            response.MessageId.Should().Be(11);
            response.OpTag.Should().Be(LdapProtocol.ExtendedResponse);
            response.ResultCode.Should().Be(LdapResultCode.UnwillingToPerform);
        }

        [Fact]
        public void RootDse_Entry_Encodes_WithDiscoveryAttributes()
        {
            // Arrange: a slice-1 RootDSE projected to the wire entry shape.
            var rootDse = new LdapRootDse(
                NamingContexts: new[] { "dc=example,dc=com" },
                SupportedLdapVersion: new[] { "3" },
                SubschemaSubentry: LdapDirectoryModel.SubschemaSubentryDn,
                VendorName: LdapDirectoryModel.VendorName);
            var entry = LdapDirectoryEntries.RootDse(rootDse);
            var bytes = LdapMessageWriter.SearchResultEntry(1, entry);

            // Act: parse the envelope + the entry attributes back off the wire.
            LdapWire.ParseResponse(bytes).OpTag.Should().Be(LdapProtocol.SearchResultEntry);
            var (dn, attributes) = ParseSearchEntry(bytes);

            // Assert: the RootDSE DN is empty and it advertises its naming context + subschema pointer.
            dn.Should().BeEmpty();
            attributes["namingContexts"].Should().Equal("dc=example,dc=com");
            attributes["subschemaSubentry"].Should().Equal(LdapDirectoryModel.SubschemaSubentryDn);
            attributes["supportedLDAPVersion"].Should().Equal("3");
        }

        [Fact]
        public void Subschema_Entry_Encodes_WithClassesAndAttributeTypes()
        {
            var subschema = new LdapSubschema(
                ObjectClasses: new[] { "inetOrgPerson", "organizationalUnit" },
                AttributeTypes: new[]
                {
                    new LdapAttributeType("cn", LdapSyntax.DirectoryString),
                    new LdapAttributeType("uidNumber", LdapSyntax.Integer),
                });
            var bytes = LdapMessageWriter.SearchResultEntry(1, LdapDirectoryEntries.Subschema(subschema));

            var (dn, attributes) = ParseSearchEntry(bytes);

            dn.Should().Be(LdapDirectoryModel.SubschemaSubentryDn);
            attributes["objectClasses"].Should().Contain("inetOrgPerson");
            attributes["attributeTypes"].Should().Contain("cn").And.Contain("uidNumber");
        }

        // Decodes a SearchResultEntry envelope back into its DN + attribute→values map.
        private static (string Dn, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes) ParseSearchEntry(byte[] envelope)
        {
            var outer = new BerCursor(envelope, 0, envelope.Length);
            var seq = outer.Child(outer.ReadElement(LdapProtocol.Sequence));
            seq.ReadElement(LdapProtocol.Integer); // messageID
            var entry = seq.Child(seq.ReadElement(LdapProtocol.SearchResultEntry));
            var dn = entry.String(entry.ReadElement(LdapProtocol.OctetString));

            var attributes = new Dictionary<string, IReadOnlyList<string>>();
            var attrList = entry.Child(entry.ReadElement(LdapProtocol.Sequence));
            while (attrList.HasMore)
            {
                var attribute = attrList.Child(attrList.ReadElement(LdapProtocol.Sequence));
                var type = attribute.String(attribute.ReadElement(LdapProtocol.OctetString));
                var values = new List<string>();
                var valueSet = attribute.Child(attribute.ReadElement(LdapProtocol.Set));
                while (valueSet.HasMore)
                    values.Add(valueSet.String(valueSet.ReadElement(LdapProtocol.OctetString)));
                attributes[type] = values;
            }
            return (dn, attributes);
        }
    }
}
