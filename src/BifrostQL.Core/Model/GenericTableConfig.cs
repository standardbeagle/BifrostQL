namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Configuration for the generic table query feature (_table).
    /// Disabled by default; must be explicitly enabled via model metadata "generic-table: enabled".
    /// </summary>
    public sealed class GenericTableConfig
    {
        public const string MetadataKey = "generic-table";
        public const string MetadataEnabled = "enabled";
        public const string RoleMetadataKey = "generic-table-role";
        public const string MaxRowsMetadataKey = "generic-table-max-rows";
        public const string AllowedTablesMetadataKey = "generic-table-allowed";
        public const string DeniedTablesMetadataKey = "generic-table-denied";

        public const string DefaultRequiredRole = "bifrost-admin";
        public const int DefaultMaxRows = 1000;

        public bool Enabled { get; init; }
        public string RequiredRole { get; init; } = DefaultRequiredRole;
        public int MaxRows { get; init; } = DefaultMaxRows;
        public IReadOnlyCollection<string>? AllowedTables { get; init; }
        public IReadOnlyCollection<string>? DeniedTables { get; init; }

        public bool IsTableAllowed(string tableName)
        {
            if (!Enabled)
                return false;

            if (DeniedTables != null && DeniedTables.Any(d =>
                    string.Equals(d, tableName, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (AllowedTables != null)
                return AllowedTables.Any(a =>
                    string.Equals(a, tableName, StringComparison.OrdinalIgnoreCase));

            return true;
        }

        public static GenericTableConfig FromModel(IDbModel model)
        {
            var enabled = string.Equals(
                model.GetMetadataValue(MetadataKey),
                MetadataEnabled,
                StringComparison.OrdinalIgnoreCase);

            var role = model.GetMetadataValue(RoleMetadataKey) ?? DefaultRequiredRole;

            var maxRowsStr = model.GetMetadataValue(MaxRowsMetadataKey);
            var maxRows = int.TryParse(maxRowsStr, out var m) && m > 0 ? m : DefaultMaxRows;

            var allowedStr = model.GetMetadataValue(AllowedTablesMetadataKey);
            var allowed = string.IsNullOrWhiteSpace(allowedStr)
                ? null
                : (IReadOnlyCollection<string>)allowedStr
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var deniedStr = model.GetMetadataValue(DeniedTablesMetadataKey);
            var denied = string.IsNullOrWhiteSpace(deniedStr)
                ? null
                : (IReadOnlyCollection<string>)deniedStr
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new GenericTableConfig
            {
                Enabled = enabled,
                RequiredRole = role,
                MaxRows = maxRows,
                AllowedTables = allowed,
                DeniedTables = denied,
            };
        }
    }
}
