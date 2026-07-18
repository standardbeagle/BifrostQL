using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Modules;

namespace BifrostQL.Integration.Test.Prometheus
{
    /// <summary>
    /// A query observer wired into the live read pipeline that records the executed SQL of every
    /// aggregate the scrape issues (at <see cref="QueryPhase.AfterExecute"/> — the intent read path
    /// notifies Transformed + AfterExecute, and only AfterExecute carries the generated
    /// <see cref="QueryObserverContext.Sql"/>) and, optionally, HOLDS the in-flight query behind a
    /// gate so concurrent scrapes are forced to coalesce onto one collection — the endpoint-level
    /// single-flight proof. The AfterExecute notification is awaited inside the collector's factory,
    /// so blocking here keeps the single-flight slot open while the sibling scrapes pile onto it.
    /// Unlike the engine self-metrics observer, it does NOT skip scrape-internal contexts, so it sees
    /// exactly the queries the scrape itself runs.
    /// </summary>
    public sealed class RecordingQueryObserver : IQueryObserver
    {
        private readonly ConcurrentQueue<string> _sql = new();
        private readonly ConcurrentDictionary<string, int> _byTable = new();
        private Func<Task>? _gate;

        public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };

        /// <summary>Every SQL statement observed, in arrival order.</summary>
        public IReadOnlyList<string> ObservedSql => _sql.ToArray();

        /// <summary>Count of aggregate queries observed for a given DB table name.</summary>
        public int CountFor(string dbTableName) => _byTable.TryGetValue(dbTableName, out var n) ? n : 0;

        /// <summary>The last SQL observed for a given DB table name (null if none).</summary>
        public string? SqlFor(string dbTableName) =>
            _sql.LastOrDefault(s => s.Contains(dbTableName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Installs a gate every observed query awaits before proceeding (single-flight test).</summary>
        public void GateOn(Func<Task> gate) => _gate = gate;

        public async ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
        {
            if (phase != QueryPhase.AfterExecute)
                return;

            if (context.Sql is { } sql)
                _sql.Enqueue(sql);
            _byTable.AddOrUpdate(context.Table.DbName, 1, (_, n) => n + 1);

            if (_gate is { } gate)
                await gate();
        }
    }
}
