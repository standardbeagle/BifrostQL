using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// Pure mapper: one drained outbox row (the <see cref="MetadataKeys.Cdc.OutboxColumns"/>
    /// contract) → a CloudEvents 1.0 JSON envelope. No I/O, no DB access, no HTTP, no clock —
    /// the event <c>time</c> is taken from the row's <c>created_at</c> column, never
    /// <see cref="DateTime.Now"/>.
    ///
    /// Only the contract columns are read. The row body travels as the CloudEvents
    /// <c>data</c> member sourced solely from the captured <c>payload</c> column; a
    /// Bifrost-internal exception message or raw driver text is never read from the row
    /// nor placed into the envelope (protocol-adapter-security rule 3).
    /// </summary>
    public static class CloudEventEnvelope
    {
        /// <summary>CloudEvents spec version this builder emits.</summary>
        public const string SpecVersion = "1.0";

        /// <summary>The event body is always JSON (the captured payload).</summary>
        public const string DataContentType = "application/json";

        /// <summary>Namespace stem for the CloudEvents <c>type</c> (<c>bifrostql.&lt;schema&gt;.&lt;table&gt;.&lt;op&gt;</c>).</summary>
        private const string TypePrefix = "bifrostql";

        /// <summary>
        /// CloudEvents extension attribute carrying the tenant. Extension names must be
        /// lowercase alphanumeric per the CloudEvents naming rules; <c>tenant</c> complies.
        /// </summary>
        public const string TenantExtensionAttribute = "tenant";

        /// <summary>
        /// Maps one outbox row to a CloudEvents 1.0 envelope. <paramref name="subject"/>
        /// is the row's primary key: the drain step that reads the outbox holds the
        /// <c>DbModel</c> and computes it from the payload's key columns, so this pure
        /// builder need not carry table-schema knowledge to identify the key.
        /// </summary>
        public static JsonObject Build(IReadOnlyDictionary<string, object?> outboxRow, string subject)
        {
            if (outboxRow is null)
                throw new ArgumentNullException(nameof(outboxRow));
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException(
                    "A CloudEvents subject (the row primary key) is required.", nameof(subject));

            var id = RequireString(outboxRow, MetadataKeys.Cdc.ColId);
            var aggregate = RequireString(outboxRow, MetadataKeys.Cdc.ColAggregate);
            var op = RequireString(outboxRow, MetadataKeys.Cdc.ColOp).ToLowerInvariant();
            var time = FormatRfc3339Utc(Require(outboxRow, MetadataKeys.Cdc.ColCreatedAt));

            var envelope = new JsonObject
            {
                ["specversion"] = SpecVersion,
                ["id"] = id,
                // The aggregate (qualified source table) identifies the producing context.
                ["source"] = aggregate,
                ["type"] = $"{TypePrefix}.{aggregate}.{op}",
                ["subject"] = subject,
                ["time"] = time,
                ["datacontenttype"] = DataContentType,
            };

            // tenant is an OPTIONAL CloudEvents extension attribute: omit it entirely when
            // the row has no tenant — never emit an explicit null.
            var tenant = OptionalString(outboxRow, MetadataKeys.Cdc.ColTenant);
            if (!string.IsNullOrWhiteSpace(tenant))
                envelope[TenantExtensionAttribute] = tenant;

            // The event body is EXACTLY the captured payload JSON. Nothing else is ever
            // placed here — no internal exception text, no driver detail.
            envelope["data"] = ParseJson(Require(outboxRow, MetadataKeys.Cdc.ColPayload));

            return envelope;
        }

        /// <summary>Convenience: <see cref="Build"/> serialized to a JSON string.</summary>
        public static string Serialize(IReadOnlyDictionary<string, object?> outboxRow, string subject)
            => Build(outboxRow, subject).ToJsonString();

        private static object Require(IReadOnlyDictionary<string, object?> row, string column)
        {
            if (!row.TryGetValue(column, out var value) || value is null || value is DBNull)
                throw new ArgumentException(
                    $"Outbox row is missing required column '{column}'.", nameof(row));
            return value;
        }

        private static string RequireString(IReadOnlyDictionary<string, object?> row, string column)
        {
            var text = Convert.ToString(Require(row, column), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException(
                    $"Outbox row column '{column}' is blank.", nameof(row));
            return text;
        }

        private static string? OptionalString(IReadOnlyDictionary<string, object?> row, string column)
        {
            if (!row.TryGetValue(column, out var value) || value is null || value is DBNull)
                return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // RFC3339 / ISO-8601 in UTC with a trailing 'Z', as CloudEvents `time` requires.
        private static string FormatRfc3339Utc(object value)
        {
            var utc = value switch
            {
                DateTimeOffset dto => dto.UtcDateTime,
                DateTime { Kind: DateTimeKind.Utc } dt => dt,
                DateTime { Kind: DateTimeKind.Local } dt => dt.ToUniversalTime(),
                // A DB DATETIME2 reads back as Unspecified; the writer stored UtcNow, so treat as UTC.
                DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                _ => throw new ArgumentException(
                    $"Outbox '{MetadataKeys.Cdc.ColCreatedAt}' must be a DateTime/DateTimeOffset, " +
                    $"was '{value.GetType().Name}'."),
            };

            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        }

        // The payload column holds a JSON document (string from the DB, or an already
        // materialized node). Parse it into structured `data`; a JsonException on a
        // malformed payload is our own captured data, never leaked internal text.
        private static JsonNode? ParseJson(object value)
        {
            return value switch
            {
                JsonNode node => node.DeepClone(),
                string s => JsonNode.Parse(s),
                _ => JsonNode.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"),
            };
        }
    }
}
