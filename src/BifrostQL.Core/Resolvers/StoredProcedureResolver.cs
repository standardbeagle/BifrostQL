using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    public sealed class StoredProcedureResolver : IBifrostResolver, IFieldResolver
    {
        private readonly DbStoredProcedure _proc;

        public StoredProcedureResolver(DbStoredProcedure proc)
        {
            _proc = proc;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;

            var input = context.HasArgument("input")
                ? context.GetArgument<Dictionary<string, object?>>("input")
                : null;

            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = _proc.FullDbRef;
                cmd.CommandType = CommandType.StoredProcedure;

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

                foreach (var (paramName, dbParam) in outputParams)
                {
                    result[paramName] = dbParam.Value == DBNull.Value ? null : dbParam.Value;
                }

                return result;
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(ex.Message, ex);
            }
        }

        private void AddInputParameters(DbCommand cmd, Dictionary<string, object?>? input)
        {
            foreach (var param in _proc.InputParameters)
            {
                object? value = null;
                input?.TryGetValue(param.GraphQlName, out value);

                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = $"@{param.DbName}";
                dbParam.Value = value ?? DBNull.Value;
                dbParam.Direction = param.Direction == ParameterDirection.InputOutput
                    ? ParameterDirection.InputOutput
                    : ParameterDirection.Input;
                cmd.Parameters.Add(dbParam);
            }
        }

        private List<(string paramName, DbParameter dbParam)> AddOutputParameters(DbCommand cmd)
        {
            var outputParams = new List<(string, DbParameter)>();

            foreach (var param in _proc.OutputParameters)
            {
                if (param.Direction == ParameterDirection.InputOutput)
                {
                    // Already added to cmd in AddInputParameters, but track for output collection
                    var existing = cmd.Parameters[$"@{param.DbName}"];
                    outputParams.Add((param.GraphQlName, (DbParameter)existing!));
                    continue;
                }

                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = $"@{param.DbName}";
                dbParam.Direction = param.Direction;
                dbParam.Value = DBNull.Value;
                dbParam.Size = GetDefaultSize(param.DataType);
                cmd.Parameters.Add(dbParam);
                outputParams.Add((param.GraphQlName, dbParam));
            }

            return outputParams;
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        private static int GetDefaultSize(string dataType) => dataType switch
        {
            "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" => 4000,
            "varbinary" or "binary" => 8000,
            _ => 0,
        };
    }
}
