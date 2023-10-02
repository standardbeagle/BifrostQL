using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    internal class DbAggregateResolver : IFieldResolver
    {
        private readonly IDbTable _table;

        public DbAggregateResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            if (context == null)
                throw new ExecutionError("Invalid context");
            var operation = context.GetArgument<string>("operation");
            if (string.IsNullOrWhiteSpace(operation))
                throw new ExecutionError($"operation is empty while querying table: {_table.GraphQlName}");
            var value = context.GetArgument<string>("value");
            var dbValue = _table.Columns.First(c => c.GraphQlName == value).DbName;
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"] ?? throw new InvalidDataException("connection factory is not configured"));
            await using var conn = conFactory.GetConnection();
            if (conn == null) throw new ExecutionError("Invalid database configuration");

            try
            {
                conn.Open();
                var cmd = new SqlCommand(
                    $"SELECT {operation}([{dbValue}]) FROM [{_table.TableSchema}].[{_table.DbName}];",
                    conn);
                var result = await cmd.ExecuteScalarAsync();
                return result;
            }
            catch (Exception ex)
            {
                throw new ExecutionError(ex.Message);
            }
        }
    }
}
