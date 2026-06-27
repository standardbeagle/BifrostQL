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

    public async ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var errors = new List<string>();

        if (IsTableValidationEnabled(table) || table.Columns.Any(IsColumnValidationEnabled))
            ValidateStandardMetadata(table, mutationType, data, errors);

        await RunPluginValidatorsAsync(table, mutationType, data, context, errors);

        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            Errors = errors.ToArray(),
        };
    }

    private async ValueTask RunPluginValidatorsAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        List<string> errors)
    {
        foreach (var providerName in ValidationPlugins(table.GetMetadataValue(MetadataKeys.Validation.Plugin)))
            await RunProviderAsync(providerName, table, mutationType, data, context, columnName: null, errors);

        foreach (var column in table.Columns)
        {
            foreach (var providerName in ValidationPlugins(column.GetMetadataValue(MetadataKeys.Validation.Plugin)))
                await RunProviderAsync(providerName, table, mutationType, data, context, column.ColumnName, errors);
        }
    }

    private async ValueTask RunProviderAsync(
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

        errors.AddRange(await provider.ValidateAsync(new ServerValidationContext
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

            var rules = ValidationRules.ForColumn(column);

            if (rules.RequiredExplicit && (mutationType == MutationType.Insert || valuePresent))
            {
                if (!valuePresent || IsMissing(value))
                    errors.Add($"{column.GraphQlName} is required.");
            }

            if (!valuePresent || IsMissing(value))
                continue;

            ValidateLength(column, rules, value, errors);
            ValidateRange(column, rules, value, errors);
            ValidatePattern(column, rules, value, errors);
            ValidateInputType(column, rules, value, errors);
        }
    }

    private static void ValidateLength(ColumnDto column, ValidationRules rules, object? value, List<string> errors)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (text == null)
            return;

        if (rules.MinLength is { } minLength && text.Length < minLength)
            errors.Add($"{column.GraphQlName} must be at least {minLength} characters.");

        if (rules.MaxLength is { } maxLength && text.Length > maxLength)
            errors.Add($"{column.GraphQlName} must be at most {maxLength} characters.");
    }

    private static void ValidateRange(ColumnDto column, ValidationRules rules, object? value, List<string> errors)
    {
        // Temporal values compare against min/max parsed as dates; everything
        // else compares as decimal. A min/max that parses neither way is ignored.
        // GraphQL date inputs frequently arrive as strings, so when the bounds
        // are dates, string values are parsed as dates too.
        var boundsAreDates = rules.TryMinDate(out _) || rules.TryMaxDate(out _);
        if (boundsAreDates && value is string text &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            value = parsed;

        if (TryDate(value, out var dateValue))
        {
            if (rules.TryMinDate(out var minDate) && dateValue < minDate)
                errors.Add($"{column.GraphQlName} must be on or after {minDate:yyyy-MM-dd}.");

            if (rules.TryMaxDate(out var maxDate) && dateValue > maxDate)
                errors.Add($"{column.GraphQlName} must be on or before {maxDate:yyyy-MM-dd}.");
            return;
        }

        if (!TryDecimal(value, out var numeric))
            return;

        if (rules.TryMinDecimal(out var min) && numeric < min)
            errors.Add($"{column.GraphQlName} must be at least {min}.");

        if (rules.TryMaxDecimal(out var max) && numeric > max)
            errors.Add($"{column.GraphQlName} must be at most {max}.");

        // Step grid: the value must be an integral number of steps from the base
        // (min when present, else 0), mirroring the HTML number input's step attribute.
        if (rules.TryStepDecimal(out var step) && step > 0)
        {
            var origin = rules.TryMinDecimal(out var baseMin) ? baseMin : 0m;
            var stepsFromOrigin = (numeric - origin) / step;
            if (Math.Abs(stepsFromOrigin - Math.Round(stepsFromOrigin)) > 0.0000001m)
                errors.Add($"{column.GraphQlName} must be in increments of {step}.");
        }
    }

    // Bounds a single pattern match so a pathological (ReDoS) metadata pattern
    // can't hang a mutation.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static void ValidatePattern(ColumnDto column, ValidationRules rules, object? value, List<string> errors)
    {
        if (rules.Pattern == null)
            return;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        // Anchor the pattern exactly like the HTML5 `pattern` attribute, the React
        // client validator, and the legacy form validator: a full-string match,
        // not a substring one. Without this the server is MORE permissive than the
        // client/HTML form, so a value the UI rejects would still be accepted here.
        var pattern = rules.Pattern.StartsWith('^') ? rules.Pattern : $"^(?:{rules.Pattern})$";

        try
        {
            if (!Regex.IsMatch(text, pattern, RegexOptions.None, RegexTimeout))
                errors.Add(rules.PatternMessage ?? $"{column.GraphQlName} is invalid.");
        }
        catch (RegexMatchTimeoutException)
        {
            errors.Add($"{column.GraphQlName} could not be validated (pattern too complex).");
        }
        catch (ArgumentException)
        {
            errors.Add($"{column.GraphQlName} has an invalid validation pattern.");
        }
    }

    private static void ValidateInputType(ColumnDto column, ValidationRules rules, object? value, List<string> errors)
    {
        if (rules.InputType == null)
            return;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(text))
            return;

        if (string.Equals(rules.InputType, "email", StringComparison.OrdinalIgnoreCase) && !IsValidEmail(text))
            errors.Add($"{column.GraphQlName} must be a valid email address.");
        else if (string.Equals(rules.InputType, "url", StringComparison.OrdinalIgnoreCase) && !IsValidUrl(text))
            errors.Add($"{column.GraphQlName} must be a valid URL.");
    }

    private static bool IsValidEmail(string value)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsTableValidationEnabled(IDbTable table)
        => IsEnabled(table.GetMetadataValue(MetadataKeys.Validation.Server));

    private static bool IsColumnValidationEnabled(ColumnDto column)
        => IsEnabled(column.GetMetadataValue(MetadataKeys.Validation.Server));

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

    private static bool TryDate(object? raw, out DateTime value)
    {
        switch (raw)
        {
            case DateTime dt:
                value = dt;
                return true;
            case DateTimeOffset dto:
                value = dto.UtcDateTime;
                return true;
            case DateOnly d:
                value = d.ToDateTime(TimeOnly.MinValue);
                return true;
            default:
                value = default;
                return false;
        }
    }

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
