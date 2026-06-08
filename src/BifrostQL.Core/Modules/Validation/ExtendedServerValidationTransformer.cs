using System.Globalization;
using System.Text.RegularExpressions;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Validation;

public sealed class ExtendedServerValidationTransformer : IMutationTransformer, IModuleNamed
{
    private readonly IReadOnlyList<IServerValidationProvider> _providers;

    public ExtendedServerValidationTransformer()
        : this(Array.Empty<IServerValidationProvider>())
    {
    }

    public ExtendedServerValidationTransformer(IEnumerable<IServerValidationProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public int Priority => 200;

    public string ModuleName => MetadataKeys.Validation.Server;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
        => mutationType is MutationType.Insert or MutationType.Update
           && (IsTableValidationEnabled(table)
               || table.Columns.Any(IsColumnValidationEnabled)
               || HasPluginValidation(table));

    public MutationTransformResult Transform(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var errors = new List<string>();

        if (IsTableValidationEnabled(table))
            ValidateStandardMetadata(table, mutationType, data, errors);

        RunPluginValidators(table, mutationType, data, context, errors);

        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            Errors = errors.ToArray(),
        };
    }

    private void RunPluginValidators(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        List<string> errors)
    {
        foreach (var providerName in ValidationPlugins(table.GetMetadataValue(MetadataKeys.Validation.Plugin)))
            RunProvider(providerName, table, mutationType, data, context, columnName: null, errors);

        foreach (var column in table.Columns)
        {
            foreach (var providerName in ValidationPlugins(column.GetMetadataValue(MetadataKeys.Validation.Plugin)))
                RunProvider(providerName, table, mutationType, data, context, column.ColumnName, errors);
        }
    }

    private void RunProvider(
        string providerName,
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string? columnName,
        List<string> errors)
    {
        var provider = _providers.FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            errors.Add($"Server validation provider '{providerName}' is not registered.");
            return;
        }

        errors.AddRange(provider.Validate(new ServerValidationContext
        {
            Model = context.Model,
            Table = table,
            MutationType = mutationType,
            Data = data,
            UserContext = context.UserContext,
            ColumnName = columnName,
            Services = context.Services,
        }));
    }

    private static void ValidateStandardMetadata(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        List<string> errors)
    {
        foreach (var column in table.Columns)
        {
            var enabled = IsColumnValidationEnabled(column);
            if (!enabled && !IsTableValidationEnabled(table))
                continue;

            var valuePresent = data.TryGetValue(column.ColumnName, out var value)
                || data.TryGetValue(column.GraphQlName, out value);

            if (IsRequired(column) && (mutationType == MutationType.Insert || valuePresent))
            {
                if (!valuePresent || IsMissing(value))
                    errors.Add($"{column.GraphQlName} is required.");
            }

            if (!valuePresent || IsMissing(value))
                continue;

            ValidateLength(column, value, errors);
            ValidateRange(column, value, errors);
            ValidatePattern(column, value, errors);
        }
    }

    private static void ValidateLength(ColumnDto column, object? value, List<string> errors)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (text == null)
            return;

        if (TryInt(column.GetMetadataValue(MetadataKeys.Validation.MinLength), out var minLength)
            && text.Length < minLength)
            errors.Add($"{column.GraphQlName} must be at least {minLength} characters.");

        if (TryInt(column.GetMetadataValue(MetadataKeys.Validation.MaxLength), out var maxLength)
            && text.Length > maxLength)
            errors.Add($"{column.GraphQlName} must be at most {maxLength} characters.");
    }

    private static void ValidateRange(ColumnDto column, object? value, List<string> errors)
    {
        if (!TryDecimal(value, out var numeric))
            return;

        if (TryDecimal(column.GetMetadataValue(MetadataKeys.Validation.Min), out var min)
            && numeric < min)
            errors.Add($"{column.GraphQlName} must be at least {min}.");

        if (TryDecimal(column.GetMetadataValue(MetadataKeys.Validation.Max), out var max)
            && numeric > max)
            errors.Add($"{column.GraphQlName} must be at most {max}.");
    }

    private static void ValidatePattern(ColumnDto column, object? value, List<string> errors)
    {
        var pattern = column.GetMetadataValue(MetadataKeys.Validation.Pattern);
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (!Regex.IsMatch(text, pattern))
            errors.Add(column.GetMetadataValue(MetadataKeys.Validation.PatternMessage) ?? $"{column.GraphQlName} is invalid.");
    }

    private static bool IsTableValidationEnabled(IDbTable table)
        => IsEnabled(table.GetMetadataValue(MetadataKeys.Validation.Server));

    private static bool IsColumnValidationEnabled(ColumnDto column)
        => IsEnabled(column.GetMetadataValue(MetadataKeys.Validation.Server));

    private static bool IsRequired(ColumnDto column)
        => IsEnabled(column.GetMetadataValue(MetadataKeys.Validation.Required));

    private static bool HasPluginValidation(IDbTable table)
        => ValidationPlugins(table.GetMetadataValue(MetadataKeys.Validation.Plugin)).Any()
           || table.Columns.Any(c => ValidationPlugins(c.GetMetadataValue(MetadataKeys.Validation.Plugin)).Any());

    private static IEnumerable<string> ValidationPlugins(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsEnabled(string? value)
        => value != null
           && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "server", StringComparison.OrdinalIgnoreCase));

    private static bool IsMissing(object? value)
        => value == null || value is DBNull || value is string text && string.IsNullOrWhiteSpace(text);

    private static bool TryInt(string? raw, out int value)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryDecimal(object? raw, out decimal value)
    {
        if (raw == null || raw is DBNull)
        {
            value = 0;
            return false;
        }

        return decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
