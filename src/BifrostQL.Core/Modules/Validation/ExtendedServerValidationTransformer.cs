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

    // 199, not the application band (>=200): server validation is on by default for every
    // write and opts out only per-table via `server-validation: off` metadata (see below).
    // Every other default-on built-in mutation transformer sits below the application
    // priority floor and is therefore always retained; keeping validation here means a
    // client-selectable profile cannot globally strip input validation from writes — it
    // stays a non-toggleable data-integrity guard, symmetric with the read path where no
    // default-on transformer lives in the toggleable band.
    public int Priority => 199;

    public string ModuleName => MetadataKeys.Validation.Server;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
        => mutationType is MutationType.Insert or MutationType.Update
           && !IsValidationDisabled(table.GetMetadataValue(MetadataKeys.Validation.Server));

    public async ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        var errors = new List<string>();

        // Validation is on by default for writes; a table opts out with
        // server-validation: off/false/disabled.
        if (!IsValidationDisabled(table.GetMetadataValue(MetadataKeys.Validation.Server)))
            ValidateStandardMetadata(table, mutationType, data, context.Model.TypeMapper, errors);

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
        ITypeMapper typeMapper,
        List<string> errors)
    {
        foreach (var column in table.Columns)
        {
            // A column can opt out individually with server-validation: off.
            if (IsValidationDisabled(column.GetMetadataValue(MetadataKeys.Validation.Server)))
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
            // Schema-derived checks: Bifrost read the schema, so anything the
            // engine would reject on type grounds is refused here with a clear
            // message instead of surfacing as a wrapped database error.
            ValidateTemporal(column, rules, value, typeMapper, errors);
            ValidateIntegerRange(column, value, typeMapper, errors);
            ValidateDecimalPrecision(column, rules, value, errors);
            ValidateBinaryLength(column, rules, value, errors);
        }
    }

    /// <summary>
    /// A temporal column's mutation input is a String on the wire (see
    /// ITypeMapper.GetGraphQlInsertTypeName), so nothing upstream has proven it
    /// parses. Refuse unparseable text, then refuse values outside the engine's
    /// storable range for the type (SQL Server datetime's 1753 floor, MySQL
    /// timestamp's 2038 ceiling) via the dialect's type mapper.
    /// </summary>
    private static void ValidateTemporal(
        ColumnDto column, ValidationRules rules, object? value, ITypeMapper typeMapper, List<string> errors)
    {
        if (rules.TemporalKind == TemporalKind.None)
            return;

        DateTime? comparable = null;
        if (value is string text)
        {
            var (parsed, ok) = ParseTemporal(rules.TemporalKind, text);
            if (!ok)
            {
                errors.Add($"{column.GraphQlName} must be a valid {TemporalNoun(rules.TemporalKind)} (ISO 8601).");
                return;
            }
            comparable = parsed;
        }
        else if (TryDate(value, out var dateValue))
        {
            comparable = dateValue;
        }

        if (comparable is null || rules.TemporalKind == TemporalKind.TimeOnly)
            return;

        if (typeMapper.GetTemporalRange(column.EffectiveDataType) is { } range
            && (comparable < range.Min || comparable > range.Max))
            errors.Add($"{column.GraphQlName} must be between {range.Min:yyyy-MM-dd} and {range.Max:yyyy-MM-dd}.");
    }

    private static (DateTime? Value, bool Ok) ParseTemporal(TemporalKind kind, string text)
    {
        switch (kind)
        {
            case TemporalKind.DateTimeOffset:
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                    ? (dto.UtcDateTime, true)
                    : (null, false);
            case TemporalKind.DateOnly:
                // A full ISO datetime is accepted for a date column (engines cast it);
                // DateTime.TryParse covers both the bare date and the datetime forms.
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? (d.Date, true)
                    : (null, false);
            case TemporalKind.TimeOnly:
                return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out _)
                       || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out _)
                    ? (null, true)
                    : (null, false);
            default:
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? (dt, true)
                    : (null, false);
        }
    }

    private static string TemporalNoun(TemporalKind kind) => kind switch
    {
        TemporalKind.DateOnly => "date",
        TemporalKind.TimeOnly => "time",
        _ => "date/time",
    };

    /// <summary>
    /// Refuses values outside the engine's storable range for the column's
    /// integer type, and non-integral values on integer columns. GraphQL scalar
    /// coercion already bounds the GraphQL path; protocol adapters (RESP, MCP,
    /// pgwire) reach this pipeline without it, so the check must live here to
    /// hold for every access method.
    /// </summary>
    private static void ValidateIntegerRange(
        ColumnDto column, object? value, ITypeMapper typeMapper, List<string> errors)
    {
        if (typeMapper.GetIntegerRange(column.EffectiveDataType) is not { } range)
            return;
        if (value is bool)
            return;
        if (!TryDecimal(value, out var numeric))
        {
            // A non-numeric value on an integer column can only fail downstream.
            if (value is string)
                errors.Add($"{column.GraphQlName} must be a whole number.");
            return;
        }

        if (decimal.Truncate(numeric) != numeric)
            errors.Add($"{column.GraphQlName} must be a whole number.");
        else if (numeric < range.Min || numeric > range.Max)
            errors.Add($"{column.GraphQlName} must be between {range.Min} and {range.Max}.");
    }

    /// <summary>
    /// Refuses a value whose integer part cannot fit the column's declared
    /// precision/scale — the overflow every engine rejects. Excess FRACTIONAL
    /// digits are deliberately allowed: engines round scale on write, and
    /// refusing what the engine accepts would break existing clients.
    /// </summary>
    private static void ValidateDecimalPrecision(
        ColumnDto column, ValidationRules rules, object? value, List<string> errors)
    {
        if (rules.NumericPrecision is not { } precision)
            return;
        if (!TryDecimal(value, out var numeric))
            return;

        var scale = rules.NumericScale ?? 0;
        var integerDigits = Math.Max(precision - scale, 0);
        var limit = IntegerPartLimit(integerDigits);
        var integerPart = Math.Abs(decimal.Truncate(numeric));
        if (integerPart >= limit)
            errors.Add(
                $"{column.GraphQlName} must have at most {integerDigits} digits before the decimal point.");
    }

    // 10^digits as a decimal without Math.Pow's double rounding; digits is at
    // most precision (<= 38), and 10^29 already exceeds decimal.MaxValue, at
    // which point no representable value can overflow the column.
    private static decimal IntegerPartLimit(int digits)
    {
        if (digits >= 29)
            return decimal.MaxValue;
        var limit = 1m;
        for (var i = 0; i < digits; i++)
            limit *= 10m;
        return limit;
    }

    /// <summary>
    /// Refuses binary payloads longer than the column's declared byte length.
    /// Raw bytes are measured directly; a string is measured by its decoded
    /// base64 length when it parses as base64 (the wire form clients send),
    /// otherwise left for the engine to arbitrate.
    /// </summary>
    private static void ValidateBinaryLength(
        ColumnDto column, ValidationRules rules, object? value, List<string> errors)
    {
        if (rules.BinaryMaxLength is not { } maxBytes)
            return;

        int? byteLength = value switch
        {
            byte[] bytes => bytes.Length,
            string text when TryBase64Length(text, out var decoded) => decoded,
            _ => null,
        };
        if (byteLength > maxBytes)
            errors.Add($"{column.GraphQlName} must be at most {maxBytes} bytes.");
    }

    private static bool TryBase64Length(string text, out int decodedLength)
    {
        var buffer = new byte[(text.Length / 4 + 1) * 3];
        if (Convert.TryFromBase64String(text, buffer, out decodedLength))
            return true;
        decodedLength = 0;
        return false;
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

    private static IEnumerable<string> ValidationPlugins(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Validation runs by default; this is the opt-out switch. Any explicit
    // "off"-like value on the server-validation key disables enforcement (at the
    // table or column level). Legacy enable values (true/enabled/server) are not
    // disable values, so existing opt-in metadata keeps validation on.
    private static bool IsValidationDisabled(string? value)
        => value != null
           && (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "0", StringComparison.Ordinal));

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
