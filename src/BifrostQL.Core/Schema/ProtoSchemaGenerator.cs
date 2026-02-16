using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    public static class ProtoSchemaGenerator
    {
        public static string GenerateProto(IDbModel model)
        {
            var sb = new StringBuilder();
            sb.AppendLine("syntax = \"proto3\";");
            sb.AppendLine();
            sb.AppendLine("package bifrostql;");
            sb.AppendLine();

            foreach (var table in model.Tables)
            {
                AppendTableMessage(sb, table);
                sb.AppendLine();
            }

            AppendBifrostMessage(sb, model);

            return sb.ToString();
        }

        internal static void AppendTableMessage(StringBuilder sb, IDbTable table)
        {
            sb.AppendLine($"message {table.GraphQlName}Row {{");
            var fieldNumber = 1;
            foreach (var column in table.Columns)
            {
                var protoType = GetProtoType(column.EffectiveDataType);
                var optional = column.IsNullable ? "optional " : "";
                sb.AppendLine($"  {optional}{protoType} {column.GraphQlName} = {fieldNumber};");
                fieldNumber++;
            }
            sb.AppendLine("}");
        }

        internal static void AppendBifrostMessage(StringBuilder sb, IDbModel model)
        {
            sb.AppendLine("message BifrostMessage {");
            sb.AppendLine("  string table = 1;");
            sb.AppendLine("  oneof payload {");
            var fieldNumber = 2;
            foreach (var table in model.Tables)
            {
                sb.AppendLine($"    {table.GraphQlName}Row {table.GraphQlName}_row = {fieldNumber};");
                fieldNumber++;
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }

        internal static string GetProtoType(string dataType)
        {
            return dataType switch
            {
                "int" => "int32",
                "smallint" => "int32",
                "tinyint" => "int32",
                "bigint" => "int64",
                "decimal" => "double",
                "float" => "double",
                "real" => "float",
                "bit" => "bool",
                "datetime" => "string",
                "datetime2" => "string",
                "datetimeoffset" => "string",
                "date" => "string",
                "time" => "string",
                "uniqueidentifier" => "string",
                "binary" => "bytes",
                "varbinary" => "bytes",
                "image" => "bytes",
                _ => "string",
            };
        }
    }
}
