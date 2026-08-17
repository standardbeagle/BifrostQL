namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Reads and decodes one LDAPMessage at a time from a byte <see cref="Stream"/>. The message
    /// is framed by its outer definite length, so the reader takes the envelope header off the
    /// socket, bounds the declared body length by <see cref="MaxMessageLength"/> BEFORE allocating,
    /// then reads exactly that many bytes and parses them in memory with a <see cref="BerCursor"/>.
    /// This makes every allocation and every recursion bounded on the UNAUTHENTICATED path:
    ///
    /// <list type="bullet">
    /// <item><b>Definite-length cap.</b> A body length beyond <see cref="MaxMessageLength"/> is
    /// refused with a protocol error before a single byte is allocated — a hostile length prefix
    /// cannot force a giant buffer (protocol-adapter-security invariant 6, tail).</item>
    /// <item><b>Nesting cap.</b> The filter tree is the recursive vector; the parse threads a depth
    /// counter and refuses to descend past <see cref="MaxNestingDepth"/> BEFORE recursing, so a
    /// deeply-nested filter never grows the physical stack toward an uncatchable
    /// <see cref="StackOverflowException"/> (invariant 6).</item>
    /// <item><b>Filter / attribute caps.</b> The total filter-node count and the requested-attribute
    /// count are each bounded, so a single (already size-capped) message cannot smuggle a
    /// pathological node/attribute explosion.</item>
    /// </list>
    ///
    /// <para>Every malformed input raises <see cref="LdapProtocolException"/> — never an unhandled
    /// throw that would reach Kestrel.</para>
    /// </summary>
    internal sealed class LdapMessageReader
    {
        public LdapMessageReader(int maxMessageLength, int maxNestingDepth, int maxFilterComponents, int maxSearchAttributes)
        {
            MaxMessageLength = maxMessageLength;
            MaxNestingDepth = maxNestingDepth;
            MaxFilterComponents = maxFilterComponents;
            MaxSearchAttributes = maxSearchAttributes;
        }

        public int MaxMessageLength { get; }
        public int MaxNestingDepth { get; }
        public int MaxFilterComponents { get; }
        public int MaxSearchAttributes { get; }

        /// <summary>
        /// Reads the next LDAPMessage, or <c>null</c> on a clean end-of-stream between messages
        /// (the peer closed). A partial header/body at EOF is a protocol violation, not a clean close.
        /// </summary>
        public async Task<LdapRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
        {
            var first = await ReadByteAsync(stream, ct);
            if (first < 0)
                return null; // clean EOF between messages
            if (first != LdapProtocol.Sequence)
                throw new LdapProtocolException($"expected an LDAPMessage SEQUENCE (0x30) but found 0x{first:X2}.");

            var length = await ReadDefiniteLengthAsync(stream, ct);
            if (length > MaxMessageLength)
                throw new LdapProtocolException($"LDAPMessage length {length} exceeds the {MaxMessageLength}-byte cap.");

            var body = await ReadExactAsync(stream, length, ct);
            return Decode(body);
        }

        // ---- decode (pure, over the in-memory body) ----

        private LdapRequest Decode(byte[] body)
        {
            var cursor = new BerCursor(body, 0, body.Length);

            var messageId = cursor.Int32(cursor.ReadElement(LdapProtocol.Integer));
            if (messageId < 0)
                throw new LdapProtocolException($"LDAPMessage ID {messageId} must be non-negative.");

            if (!cursor.HasMore)
                throw new LdapProtocolException("LDAPMessage has no protocolOp.");
            var opElement = cursor.ReadElement();
            var operation = DecodeOperation(cursor, opElement);

            var controls = cursor.HasMore && cursor.PeekTag == LdapProtocol.ControlsTag
                ? DecodeControls(cursor, cursor.ReadElement())
                : Array.Empty<LdapControl>();

            return new LdapRequest(messageId, opElement.Tag, operation, controls);
        }

        private LdapOperation DecodeOperation(BerCursor parent, BerElement op) => op.Tag switch
        {
            LdapProtocol.BindRequest => DecodeBind(parent.Child(op)),
            LdapProtocol.SearchRequest => DecodeSearch(parent.Child(op)),
            LdapProtocol.UnbindRequest => new LdapUnbindRequest(),
            LdapProtocol.AbandonRequest => new LdapAbandonRequest(parent.Int32(op)),
            LdapProtocol.ExtendedRequest => DecodeExtended(parent.Child(op)),
            _ => new LdapUnknownOperation(op.Tag),
        };

        private static LdapOperation DecodeBind(BerCursor bind)
        {
            var version = bind.Int32(bind.ReadElement(LdapProtocol.Integer));
            var name = bind.String(bind.ReadElement(LdapProtocol.OctetString));
            var authKind = LdapBindAuthKind.Unknown;
            byte[]? simplePassword = null;
            if (bind.HasMore)
            {
                var authChoice = bind.ReadElement();
                authKind = authChoice.Tag switch
                {
                    LdapProtocol.BindSimpleAuth => LdapBindAuthKind.Simple,
                    LdapProtocol.BindSaslAuth => LdapBindAuthKind.Sasl,
                    _ => LdapBindAuthKind.Unknown,
                };
                // The simple-auth choice ([0] primitive) IS the password octets; capture them so the
                // authenticator can verify (and then zero) them. SASL ([3] constructed) is a non-goal
                // and its credentials are never extracted here.
                if (authKind == LdapBindAuthKind.Simple)
                    simplePassword = bind.Content(authChoice);
            }
            return new LdapBindRequest(version, name, authKind, simplePassword);
        }

        private LdapOperation DecodeSearch(BerCursor search)
        {
            var baseObject = search.String(search.ReadElement(LdapProtocol.OctetString));
            var scope = (int)search.Integer(search.ReadElement(LdapProtocol.Enumerated));
            var derefAliases = (int)search.Integer(search.ReadElement(LdapProtocol.Enumerated));
            // The client's own sizeLimit / timeLimit. Both are non-negative by the protocol, and a
            // negative value is a wire violation rather than something to clamp silently: these
            // bound the server's work, so a value it cannot interpret must not be guessed at.
            var sizeLimit = NonNegative(search.Int32(search.ReadElement(LdapProtocol.Integer)), "sizeLimit");
            var timeLimit = NonNegative(search.Int32(search.ReadElement(LdapProtocol.Integer)), "timeLimit");
            var typesOnly = search.Boolean(search.ReadElement(LdapProtocol.Boolean));

            var componentBudget = MaxFilterComponents;
            var filter = DecodeFilter(search, search.ReadElement(), depth: 0, ref componentBudget);

            var attributes = new List<string>();
            if (search.HasMore)
            {
                var listElement = search.ReadElement(LdapProtocol.Sequence);
                var list = search.Child(listElement);
                while (list.HasMore)
                {
                    if (attributes.Count >= MaxSearchAttributes)
                        throw new LdapProtocolException(
                            $"SearchRequest requests more than the {MaxSearchAttributes}-attribute cap.");
                    attributes.Add(list.String(list.ReadElement(LdapProtocol.OctetString)));
                }
            }

            return new LdapSearchRequest(
                baseObject, scope, derefAliases, sizeLimit, timeLimit, typesOnly, filter, attributes);
        }

        private static int NonNegative(int value, string field) =>
            value >= 0
                ? value
                : throw new LdapProtocolException($"SearchRequest {field} {value} must be non-negative.");

        // Recursively decodes the filter tree. The depth guard runs BEFORE descending into a
        // connective's children (invariant 6): a filter nested past MaxNestingDepth is refused as a
        // clean protocol error, so the physical stack never grows past the cap. The component budget
        // bounds the total node count across the whole (already size-capped) filter.
        private LdapFilter DecodeFilter(BerCursor parent, BerElement element, int depth, ref int componentBudget)
        {
            if (--componentBudget < 0)
                throw new LdapProtocolException($"search filter exceeds the {MaxFilterComponents}-component cap.");

            switch (element.Tag)
            {
                case LdapProtocol.FilterAnd:
                    return new LdapFilter.And(DecodeFilterSet(parent, element, depth, ref componentBudget));
                case LdapProtocol.FilterOr:
                    return new LdapFilter.Or(DecodeFilterSet(parent, element, depth, ref componentBudget));
                case LdapProtocol.FilterNot:
                {
                    var inner = parent.Child(element);
                    if (!inner.HasMore)
                        throw new LdapProtocolException("'not' filter has no inner filter.");
                    return new LdapFilter.Not(DecodeFilter(inner, inner.ReadElement(), NextDepth(depth), ref componentBudget));
                }
                case LdapProtocol.FilterPresent:
                    return new LdapFilter.Present(parent.String(element));
                case LdapProtocol.FilterEqualityMatch:
                case LdapProtocol.FilterGreaterOrEqual:
                case LdapProtocol.FilterLessOrEqual:
                case LdapProtocol.FilterApproxMatch:
                {
                    var ava = parent.Child(element);
                    var attribute = ava.String(ava.ReadElement(LdapProtocol.OctetString));
                    var value = ava.Content(ava.ReadElement(LdapProtocol.OctetString));
                    return new LdapFilter.Comparison(element.Tag, attribute, value);
                }
                case LdapProtocol.FilterSubstrings:
                    return DecodeSubstrings(parent, element, ref componentBudget);
                default:
                    // extensibleMatch and any other filter type: recorded as a leaf so the tree is
                    // complete, but not modeled in detail (extensibleMatch is a declared non-goal).
                    return new LdapFilter.Other(element.Tag);
            }
        }

        /// <summary>
        /// Decodes a SubstringFilter (RFC 4511 §4.5.1.7.2):
        /// <c>SEQUENCE { type AttributeDescription, substrings SEQUENCE SIZE(1..MAX) OF CHOICE {
        /// initial [0], any [1], final [2] } }</c>.
        ///
        /// <para>Every fragment is charged against the shared component budget BEFORE it is added to
        /// the list, so the fragment sequence — which the wire declares no count for — cannot grow
        /// the parse past the same node ceiling the connective tree obeys. The list grows
        /// incrementally: nothing is pre-allocated from a client-declared size (invariant 6, tail).
        /// The grammar is enforced rather than tolerated: at most one <c>initial</c> (first) and at
        /// most one <c>final</c> (last), and at least one fragment overall. A substring assertion
        /// with no fragments is <c>(attr=*)</c> written the long way — a presence test that the
        /// client should have encoded as such — and admitting it here would give the same query two
        /// spellings with two compilation paths.</para>
        /// </summary>
        private LdapFilter DecodeSubstrings(BerCursor parent, BerElement element, ref int componentBudget)
        {
            var substring = parent.Child(element);
            var attribute = substring.String(substring.ReadElement(LdapProtocol.OctetString));

            var fragments = substring.Child(substring.ReadElement(LdapProtocol.Sequence));
            byte[]? initial = null;
            byte[]? final = null;
            var any = new List<byte[]>();
            var seen = 0;

            while (fragments.HasMore)
            {
                if (--componentBudget < 0)
                    throw new LdapProtocolException(
                        $"search filter exceeds the {MaxFilterComponents}-component cap.");

                var fragment = fragments.ReadElement();
                var value = fragments.Content(fragment);
                switch (fragment.Tag)
                {
                    case LdapProtocol.SubstringInitial:
                        if (seen != 0)
                            throw new LdapProtocolException(
                                "SubstringFilter 'initial' must be the first substring component.");
                        initial = value;
                        break;
                    case LdapProtocol.SubstringAny:
                        if (final is not null)
                            throw new LdapProtocolException(
                                "SubstringFilter 'final' must be the last substring component.");
                        any.Add(value);
                        break;
                    case LdapProtocol.SubstringFinal:
                        if (final is not null)
                            throw new LdapProtocolException(
                                "SubstringFilter carries more than one 'final' substring component.");
                        final = value;
                        break;
                    default:
                        throw new LdapProtocolException(
                            $"SubstringFilter component tag 0x{fragment.Tag:X2} is not initial/any/final.");
                }
                seen++;
            }

            if (seen == 0)
                throw new LdapProtocolException(
                    "SubstringFilter carries no substring components; use a presence filter instead.");

            return new LdapFilter.Substrings(attribute, initial, any, final);
        }

        private IReadOnlyList<LdapFilter> DecodeFilterSet(BerCursor parent, BerElement element, int depth, ref int componentBudget)
        {
            var childDepth = NextDepth(depth);
            var child = parent.Child(element);
            var children = new List<LdapFilter>();
            while (child.HasMore)
                children.Add(DecodeFilter(child, child.ReadElement(), childDepth, ref componentBudget));
            return children;
        }

        // The depth for a connective's children, refusing to descend past the cap BEFORE recursing.
        private int NextDepth(int depth)
        {
            if (depth >= MaxNestingDepth)
                throw new LdapProtocolException($"search filter nesting exceeds the {MaxNestingDepth}-level cap.");
            return depth + 1;
        }

        private static LdapOperation DecodeExtended(BerCursor extended)
        {
            var nameElement = extended.ReadElement();
            if (nameElement.Tag != LdapProtocol.ExtendedRequestName)
                throw new LdapProtocolException(
                    $"ExtendedRequest requestName must be tagged 0x{LdapProtocol.ExtendedRequestName:X2}, found 0x{nameElement.Tag:X2}.");
            return new LdapExtendedRequest(extended.String(nameElement));
        }

        private static IReadOnlyList<LdapControl> DecodeControls(BerCursor parent, BerElement controlsElement)
        {
            var controls = new List<LdapControl>();
            var seq = parent.Child(controlsElement);
            while (seq.HasMore)
            {
                var control = seq.Child(seq.ReadElement(LdapProtocol.Sequence));
                var oid = control.String(control.ReadElement(LdapProtocol.OctetString));
                var criticality = false;
                byte[]? value = null;
                if (control.HasMore && control.PeekTag == LdapProtocol.Boolean)
                    criticality = control.Boolean(control.ReadElement(LdapProtocol.Boolean));
                if (control.HasMore && control.PeekTag == LdapProtocol.OctetString)
                    value = control.Content(control.ReadElement(LdapProtocol.OctetString));
                controls.Add(new LdapControl(oid, criticality, value));
            }
            return controls;
        }

        // ---- stream primitives ----

        private async Task<int> ReadDefiniteLengthAsync(Stream stream, CancellationToken ct)
        {
            var first = await ReadByteAsync(stream, ct);
            if (first < 0)
                throw new LdapProtocolException("unexpected end of stream while reading an LDAPMessage length.");
            if (first < 0x80)
                return first;
            if (first == 0x80)
                throw new LdapProtocolException("indefinite BER length is forbidden in LDAP.");
            var byteCount = first & 0x7F;
            if (byteCount > 4)
                throw new LdapProtocolException($"LDAPMessage length prefix of {byteCount} bytes exceeds the 32-bit bound.");
            long length = 0;
            for (var i = 0; i < byteCount; i++)
            {
                var b = await ReadByteAsync(stream, ct);
                if (b < 0)
                    throw new LdapProtocolException("unexpected end of stream inside an LDAPMessage length.");
                length = (length << 8) | (uint)b;
            }
            if (length > int.MaxValue)
                throw new LdapProtocolException($"LDAPMessage length {length} exceeds the 32-bit bound.");
            return (int)length;
        }

        private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
        {
            var one = new byte[1];
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            return read == 0 ? -1 : one[0];
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
                if (read == 0)
                    throw new LdapProtocolException("unexpected end of stream inside an LDAPMessage body.");
                offset += read;
            }
            return buffer;
        }
    }
}
