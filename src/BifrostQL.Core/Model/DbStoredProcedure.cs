using System.Data;
using System.Text.RegularExpressions;

namespace BifrostQL.Core.Model
{
    public sealed class DbProcedureParameter
    {
        public string DbName { get; init; } = null!;
        public string GraphQlName { get; init; } = null!;
        public string DataType { get; init; } = null!;
        public ParameterDirection Direction { get; init; }
        public int OrdinalPosition { get; init; }
        public bool IsNullable { get; init; }

        public override string ToString() =>
            $"{DbName} ({DataType} {Direction}{(IsNullable ? " NULL" : "")})";
    }

    public sealed class DbStoredProcedure
    {
        public string DbName { get; init; } = null!;
        public string GraphQlName { get; init; } = null!;
        public string ProcedureSchema { get; init; } = null!;
        public IReadOnlyList<DbProcedureParameter> Parameters { get; init; } = Array.Empty<DbProcedureParameter>();
        public bool IsReadOnly { get; init; }

        public string FullDbRef => string.IsNullOrWhiteSpace(ProcedureSchema)
            ? $"[{DbName}]"
            : $"[{ProcedureSchema}].[{DbName}]";

        public string FullGraphQlName => ProcedureSchema == "dbo"
            ? GraphQlName
            : $"{ProcedureSchema}_{GraphQlName}";

        public string InputTypeName => $"sp_{FullGraphQlName}_Input";
        public string ResultTypeName => $"sp_{FullGraphQlName}_Result";

        public IEnumerable<DbProcedureParameter> InputParameters =>
            Parameters.Where(p => p.Direction == ParameterDirection.Input || p.Direction == ParameterDirection.InputOutput);

        public IEnumerable<DbProcedureParameter> OutputParameters =>
            Parameters.Where(p => p.Direction == ParameterDirection.Output || p.Direction == ParameterDirection.InputOutput || p.Direction == ParameterDirection.ReturnValue);

        public override string ToString() => $"[{ProcedureSchema}].[{DbName}]";

        public static bool MatchesFilter(string procedureName, string? includePattern, string? excludePattern)
        {
            if (excludePattern != null && Regex.IsMatch(procedureName, excludePattern, RegexOptions.IgnoreCase))
                return false;
            if (includePattern != null && !Regex.IsMatch(procedureName, includePattern, RegexOptions.IgnoreCase))
                return false;
            return true;
        }
    }
}
