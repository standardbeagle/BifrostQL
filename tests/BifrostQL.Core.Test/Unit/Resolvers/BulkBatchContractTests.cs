using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Resolvers
{
    public class BulkBatchContractTests
    {
        // A factory that does not opt in: implements only the required members, so the
        // BulkBatchExecutor default interface member answers.
        private sealed class MinimalConnFactory : IDbConnFactory
        {
            public DbConnection GetConnection() => throw new NotSupportedException();
            public ISqlDialect Dialect => throw new NotSupportedException();
            public ISchemaReader SchemaReader => throw new NotSupportedException();
            public ITypeMapper TypeMapper => throw new NotSupportedException();
        }

        [Fact]
        public void ConnFactory_WithoutBulkCapability_DefaultsToNullExecutor()
        {
            // A provider that never heard of bulk batches must resolve to "no executor",
            // which keeps every batch on the per-row pipeline.
            IDbConnFactory factory = new MinimalConnFactory();
            factory.BulkBatchExecutor.Should().BeNull();
        }

        [Fact]
        public void SqliteFactory_HasNoBulkExecutor()
        {
            // A real non-SQL-Server provider inherits the default: the fast path is
            // structurally unreachable for it.
            IDbConnFactory factory = new BifrostQL.Sqlite.SqliteDbConnFactory("Data Source=:memory:");
            factory.BulkBatchExecutor.Should().BeNull();
        }
    }
}
