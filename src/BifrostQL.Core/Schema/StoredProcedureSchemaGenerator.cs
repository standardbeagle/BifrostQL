using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    internal sealed class StoredProcedureSchemaGenerator
    {
        private readonly DbStoredProcedure _proc;

        public StoredProcedureSchemaGenerator(DbStoredProcedure proc)
        {
            _proc = proc;
        }

        public string GetFieldDefinition()
        {
            var args = GetArgumentList();
            var argStr = args.Length > 0 ? $"({args})" : "";
            return $"{_proc.FullGraphQlName}{argStr}: {_proc.ResultTypeName}";
        }

        public string GetResultTypeDefinition()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_proc.ResultTypeName} {{");
            builder.AppendLine("\tresultSets: [[JSON]]");
            builder.AppendLine("\taffectedRows: Int!");

            foreach (var param in _proc.OutputParameters)
            {
                builder.AppendLine($"\t{param.GraphQlName}: {SchemaGenerator.GetGraphQlTypeName(param.DataType, true)}");
            }

            builder.AppendLine("}");
            return builder.ToString();
        }

        public string GetInputTypeDefinition()
        {
            var inputParams = _proc.InputParameters.ToArray();
            if (inputParams.Length == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine($"input {_proc.InputTypeName} {{");

            foreach (var param in inputParams)
            {
                builder.AppendLine($"\t{param.GraphQlName}: {SchemaGenerator.GetGraphQlTypeName(param.DataType, param.IsNullable)}");
            }

            builder.AppendLine("}");
            return builder.ToString();
        }

        private string GetArgumentList()
        {
            var inputParams = _proc.InputParameters.ToArray();
            if (inputParams.Length == 0)
                return string.Empty;

            return $"input: {_proc.InputTypeName}";
        }
    }
}
