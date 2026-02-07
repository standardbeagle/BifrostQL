using System.Data;
using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Core.Resolvers
{
    public sealed class StoredProcedureResolver : IFieldResolver
    {
        private readonly DbStoredProcedure _proc;

        public StoredProcedureResolver(DbStoredProcedure proc)
        {
            _proc = proc;
        }

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"]
                ?? throw new InvalidDataException("connection factory is not configured"));

            var input = context.HasArgument("input")
                ? context.GetArgument<Dictionary<string, object?>>("input")
                : null;

            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand(_proc.FullDbRef, conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                AddInputParameters(cmd, input);
                var outputParams = AddOutputParameters(cmd);

                var resultSets = new List<List<Dictionary<string, object?>>>();
                int affectedRows;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    do
                    {
                        var resultSet = new List<Dictionary<string, object?>>();
                        while (await reader.ReadAsync())
                        {
                            var row = new Dictionary<string, object?>();
                            for (var i = 0; i < reader.FieldCount; i++)
                            {
                                var value = reader.GetValue(i);
                                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
                            }
                            resultSet.Add(row);
                        }
                        if (resultSet.Count > 0 || reader.FieldCount > 0)
                            resultSets.Add(resultSet);
                    } while (await reader.NextResultAsync());

                    affectedRows = reader.RecordsAffected >= 0 ? reader.RecordsAffected : 0;
                }

                var result = new Dictionary<string, object?>
                {
                    ["resultSets"] = resultSets,
                    ["affectedRows"] = affectedRows,
                };

                foreach (var (paramName, sqlParam) in outputParams)
                {
                    result[paramName] = sqlParam.Value == DBNull.Value ? null : sqlParam.Value;
                }

                return result;
            }
            catch (Exception ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
        }

        private void AddInputParameters(SqlCommand cmd, Dictionary<string, object?>? input)
        {
            foreach (var param in _proc.InputParameters)
            {
                object? value = null;
                input?.TryGetValue(param.GraphQlName, out value);

                var sqlParam = new SqlParameter
                {
                    ParameterName = $"@{param.DbName}",
                    Value = value ?? DBNull.Value,
                    Direction = param.Direction == ParameterDirection.InputOutput
                        ? ParameterDirection.InputOutput
                        : ParameterDirection.Input,
                };
                cmd.Parameters.Add(sqlParam);
            }
        }

        private List<(string paramName, SqlParameter sqlParam)> AddOutputParameters(SqlCommand cmd)
        {
            var outputParams = new List<(string, SqlParameter)>();

            foreach (var param in _proc.OutputParameters)
            {
                if (param.Direction == ParameterDirection.InputOutput)
                {
                    // Already added to cmd in AddInputParameters, but track for output collection
                    var existing = cmd.Parameters[$"@{param.DbName}"];
                    outputParams.Add((param.GraphQlName, existing));
                    continue;
                }

                var sqlParam = new SqlParameter
                {
                    ParameterName = $"@{param.DbName}",
                    Direction = param.Direction,
                    Value = DBNull.Value,
                    Size = GetDefaultSize(param.DataType),
                };
                cmd.Parameters.Add(sqlParam);
                outputParams.Add((param.GraphQlName, sqlParam));
            }

            return outputParams;
        }

        private static int GetDefaultSize(string dataType) => dataType switch
        {
            "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" => 4000,
            "varbinary" or "binary" => 8000,
            _ => 0,
        };
    }
}
