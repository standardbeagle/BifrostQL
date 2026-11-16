using System.Security.Claims;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// RESP reads against the model's <c>max-query-rows</c> ceiling and the default row window,
    /// driven over the real wire into the real read pipeline (seeded SQLite). The ceiling is applied
    /// inside SQL generation, so an adapter that sizes its own LIMIT never sees the clamp: these
    /// facts pin that a read either answers every row it was asked for or refuses explicitly.
    /// </summary>
    public sealed class RespRowCeilingTests
    {
        private const int SeededRows = 150;

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS items",
            "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            $"""
            INSERT INTO items(id, name)
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < {SeededRows})
            SELECT x, 'item-' || x FROM n
            """,
            "DROP TABLE IF EXISTS pairs",
            "CREATE TABLE pairs (a INTEGER NOT NULL, b INTEGER NOT NULL, label TEXT NOT NULL, PRIMARY KEY (a, b))",
            $"""
            INSERT INTO pairs(a, b, label)
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < {SeededRows})
            SELECT x, x % 3, 'pair-' || x FROM n
            """,
        };

        private static readonly ClaimsPrincipal Caller =
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "reader") }, authenticationType: "resp"));

        private static string[] ItemKeys(int count) =>
            Enumerable.Range(1, count).Select(i => $"items:{i}").ToArray();

        private static string[] PairKeys(int count) =>
            Enumerable.Range(1, count).Select(i => $"pairs:{i}:{i % 3}").ToArray();

        private static Task<RespRealDbHarness> StartAsync(string name, int? maxQueryRows) =>
            RespRealDbHarness.StartAsync(
                name,
                maxQueryRows is { } ceiling
                    ? new[] { $":root {{ {MetadataKeys.Model.MaxQueryRows}: {ceiling} }}" }
                    : Array.Empty<string>(),
                SeedSql);

        private static Task<RespValue> SendAsync(RespRealDbHarness harness, params string[] command) =>
            harness.RunAsync(Caller, async client =>
            {
                await client.SendCommandAsync(command);
                return await client.ReadReplyAsync() ?? throw new InvalidOperationException("connection closed");
            });

        private static IReadOnlyList<string?> BulkTexts(RespValue reply)
        {
            reply.Should().BeOfType<RespArray>();
            return ((RespArray)reply).Items!.Select(item => item switch
            {
                RespBulkString { Value: { } bytes } => Encoding.UTF8.GetString(bytes),
                _ => null,
            }).ToList();
        }

        [Fact]
        public async Task MGet_MoreKeysThanTheDefaultRowWindow_ReturnsEveryRow()
        {
            await using var harness = await StartAsync(nameof(MGet_MoreKeysThanTheDefaultRowWindow_ReturnsEveryRow), null);

            var reply = await SendAsync(harness, new[] { RespProtocol.MGet }.Concat(ItemKeys(SeededRows)).ToArray());

            var texts = BulkTexts(reply);
            texts.Should().HaveCount(SeededRows);
            texts.Count(t => t is null).Should().Be(0, "every one of the 150 keys exists and must resolve, not read as nil past row 100");
            texts[SeededRows - 1].Should().Contain("\"item-150\"");
        }

        [Fact]
        public async Task MGet_CompositeKeys_MoreThanTheDefaultRowWindow_ReturnsEveryRow()
        {
            await using var harness = await StartAsync(nameof(MGet_CompositeKeys_MoreThanTheDefaultRowWindow_ReturnsEveryRow), null);

            var reply = await SendAsync(harness, new[] { RespProtocol.MGet }.Concat(PairKeys(SeededRows)).ToArray());

            var texts = BulkTexts(reply);
            texts.Should().HaveCount(SeededRows);
            texts.Count(t => t is null).Should().Be(0, "every one of the 150 composite keys exists");
            texts[SeededRows - 1].Should().Contain("\"pair-150\"");
        }

        [Fact]
        public async Task Exists_MoreKeysThanTheDefaultRowWindow_CountsEveryRow()
        {
            await using var harness = await StartAsync(nameof(Exists_MoreKeysThanTheDefaultRowWindow_CountsEveryRow), null);

            var reply = await SendAsync(harness, new[] { RespProtocol.Exists }.Concat(ItemKeys(SeededRows)).ToArray());

            reply.Should().Be(new RespInteger(SeededRows));
        }

        [Fact]
        public async Task MGet_MoreKeysThanMaxQueryRows_IsRefusedWithTheCounts()
        {
            await using var harness = await StartAsync(nameof(MGet_MoreKeysThanMaxQueryRows_IsRefusedWithTheCounts), 5);

            var refused = await SendAsync(harness, new[] { RespProtocol.MGet }.Concat(ItemKeys(6)).ToArray());
            refused.Should().Be(new RespError("ERR too many keys for one table: 6 requested, at most 5 per command"));

            var refusedComposite = await SendAsync(harness, new[] { RespProtocol.Exists }.Concat(PairKeys(6)).ToArray());
            refusedComposite.Should().Be(new RespError("ERR too many keys for one table: 6 requested, at most 5 per command"));

            // Control: a batch that fits under the ceiling still answers every key.
            var served = BulkTexts(await SendAsync(harness, new[] { RespProtocol.MGet }.Concat(ItemKeys(5)).ToArray()));
            served.Should().HaveCount(5).And.NotContainNulls();
        }

        [Fact]
        public async Task MGet_DuplicateKeys_CountOncePerDistinctKey()
        {
            await using var harness = await StartAsync(nameof(MGet_DuplicateKeys_CountOncePerDistinctKey), 5);

            // Eight requested keys, five distinct: the batch reads five rows and fits under the ceiling.
            var keys = ItemKeys(5).Concat(new[] { "items:1", "items:2", "items:3" }).ToArray();
            var texts = BulkTexts(await SendAsync(harness, new[] { RespProtocol.MGet }.Concat(keys).ToArray()));

            texts.Should().HaveCount(8).And.NotContainNulls();
            texts[7].Should().Contain("\"item-3\"");
        }
    }
}
