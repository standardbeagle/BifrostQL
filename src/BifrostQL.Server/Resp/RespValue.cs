namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// A decoded RESP value. The closed hierarchy models exactly the RESP2 types and the
    /// RESP3 additions <c>HELLO 3</c> negotiates, so the codec is total: every case the
    /// reader can produce, the writer can encode, and vice-versa. Producers pick the
    /// concrete type; the writer serializes by pattern match. RESP3-only types must only be
    /// sent to a client that negotiated protocol 3 — the connection loop, not the codec,
    /// owns that policy.
    /// </summary>
    internal abstract record RespValue
    {
        // ---- RESP2 ----
        public static RespSimpleString Simple(string value) => new(value);
        public static RespError Err(string message) => new(message);
        public static RespInteger Int(long value) => new(value);
        public static RespBulkString Bulk(string value) => new(System.Text.Encoding.UTF8.GetBytes(value));
        public static readonly RespBulkString NullBulk = new((byte[]?)null);
        public static RespArray Arr(params RespValue[] items) => new(items);
        public static readonly RespArray NullArray = new((IReadOnlyList<RespValue>?)null);
    }

    /// <summary>RESP2 <c>+</c> Simple String — a short status line with no CR/LF.</summary>
    internal sealed record RespSimpleString(string Value) : RespValue;

    /// <summary>RESP2 <c>-</c> Error. <see cref="Message"/> already carries the error code prefix (e.g. <c>WRONGPASS …</c>).</summary>
    internal sealed record RespError(string Message) : RespValue;

    /// <summary>RESP2 <c>:</c> Integer.</summary>
    internal sealed record RespInteger(long Value) : RespValue;

    /// <summary>RESP2 <c>$</c> Bulk String; a null <see cref="Value"/> encodes the null bulk (<c>$-1</c>).</summary>
    internal sealed record RespBulkString(byte[]? Value) : RespValue;

    /// <summary>RESP2 <c>*</c> Array; a null <see cref="Items"/> encodes the null array (<c>*-1</c>).</summary>
    internal sealed record RespArray(IReadOnlyList<RespValue>? Items) : RespValue;

    /// <summary>RESP3 <c>_</c> Null.</summary>
    internal sealed record RespNull : RespValue
    {
        public static readonly RespNull Instance = new();
    }

    /// <summary>RESP3 <c>#</c> Boolean.</summary>
    internal sealed record RespBoolean(bool Value) : RespValue;

    /// <summary>RESP3 <c>,</c> Double (incl. <c>inf</c>/<c>-inf</c>/<c>nan</c>).</summary>
    internal sealed record RespDouble(double Value) : RespValue;

    /// <summary>RESP3 <c>(</c> Big number — an arbitrary-precision integer carried as its decimal digits.</summary>
    internal sealed record RespBigNumber(string Digits) : RespValue;

    /// <summary>RESP3 <c>=</c> Verbatim string: a 3-char <see cref="Format"/> (e.g. <c>txt</c>) plus <see cref="Value"/>.</summary>
    internal sealed record RespVerbatimString(string Format, string Value) : RespValue;

    /// <summary>RESP3 <c>%</c> Map — ordered key/value pairs.</summary>
    internal sealed record RespMap(IReadOnlyList<KeyValuePair<RespValue, RespValue>> Entries) : RespValue;

    /// <summary>RESP3 <c>~</c> Set.</summary>
    internal sealed record RespSet(IReadOnlyList<RespValue> Items) : RespValue;

    /// <summary>RESP3 <c>&gt;</c> Push — an out-of-band message frame.</summary>
    internal sealed record RespPush(IReadOnlyList<RespValue> Items) : RespValue;
}
