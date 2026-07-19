using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Modules.ComputedColumns;

public static class ComputedColumnConfigCollector
{
    /// <summary>
    /// Collects computed-column definitions for a table without model context.
    /// EAV <c>_meta</c> synthesis is skipped (it requires the model's
    /// <see cref="IDbModel.EavConfigs"/>); callers that must expose <c>_meta</c>
    /// use the <see cref="FromTable(IDbTable, IDbModel?)"/> overload.
    /// </summary>
    public static IReadOnlyList<ComputedColumnDefinition> FromTable(IDbTable table)
        => FromTable(table, null);

    public static IReadOnlyList<ComputedColumnDefinition> FromTable(IDbTable table, IDbModel? model)
    {
        var result = new List<ComputedColumnDefinition>();
        result.AddRange(ParseSql(table.GetMetadataValue(MetadataKeys.Computed.Sql)));
        result.AddRange(ParseExpression(table.GetMetadataValue(MetadataKeys.Computed.Expression)));
        result.AddRange(ParseProvider(table.GetMetadataValue(MetadataKeys.Computed.Provider)));
        result.AddRange(FileFolderComputedColumnCollector.FromTable(table));
        AddStateMachineTransitions(table, result);
        AddEavMeta(table, model, result);
        GuardRawSql(table, model, result);
        return result;
    }

    /// <summary>
    /// Admin-gates the deprecated raw-SQL (<see cref="ComputedColumnKind.Sql"/>) kind at
    /// config-collection time: a table carrying a raw-SQL computed column is REJECTED unless
    /// the model turns on raw SQL (<c>raw-sql: enabled</c>) — the same model-level privilege
    /// signal <see cref="Schema.SchemaGenerator.IsRawSqlEnabled"/> reads for the raw-query
    /// feature. Enabling raw SQL is a deliberate operator/admin decision, so a config assembled
    /// without it can never smuggle a verbatim-SQL computed column onto a table; the failure
    /// surfaces here (schema build / dispatch collection), not at query time.
    ///
    /// The model-LESS overload skips the check: it has no privilege signal to read and is a
    /// lower-level parsing primitive (used by <see cref="Model.ModelConfigValidator"/> and the
    /// metadata-grammar path). The runtime paths that turn metadata into a live schema
    /// (<see cref="Schema.TableSchemaGenerator"/>, <see cref="Resolvers.BifrostDispatcher"/>)
    /// all pass the model, so the gate holds where it matters.
    /// </summary>
    private static void GuardRawSql(IDbTable table, IDbModel? model, List<ComputedColumnDefinition> result)
    {
        if (model is null)
            return;

        // Check for a raw-SQL column BEFORE reading model metadata: the vast majority of tables
        // carry none, and the model's metadata is only relevant when one is present.
        // CS0618: the deprecated raw-SQL kind stays admin-gated and functional; this gate is the
        // very code that admin-gates it, so its reference to the [Obsolete] member is intentional.
#pragma warning disable CS0618
        var rawColumn = result.FirstOrDefault(d => d.Kind == ComputedColumnKind.Sql);
#pragma warning restore CS0618
        if (rawColumn is null)
            return;

        if (Utils.MetadataSwitch.Parse(model.GetMetadataValue(MetadataKeys.RawSql.Enabled), defaultValue: false))
            return;

        throw new BifrostExecutionError(
            $"Table '{table.GraphQlName}' declares a raw-SQL computed column '{rawColumn.Name}' via the " +
            $"deprecated '{MetadataKeys.Computed.Sql}' metadata, but raw SQL is not enabled on this model. " +
            $"Raw-SQL computed columns are an injection surface and are admin-gated: enable " +
            $"'{MetadataKeys.RawSql.Enabled}' at the model level, or migrate the column to the structured " +
            $"'{MetadataKeys.Computed.Expression}' kind, which has no verbatim-SQL path.");
    }

    /// <summary>
    /// Emits the read-only <c>_meta</c> provider column on tables that are EAV
    /// parents (an <see cref="Model.AppSchema.EavConfig"/> names them as parent).
    /// Requires model context — skipped when <paramref name="model"/> is null.
    /// The single PK column (DB name) is declared as the dependency so it is
    /// projected for the provider to read. Composite-PK parents are skipped (the
    /// provider also guards this), since the meta foreign key is single-column.
    /// </summary>
    private static void AddEavMeta(IDbTable table, IDbModel? model, List<ComputedColumnDefinition> result)
    {
        if (model is null)
            return;

        var isParent = model.EavConfigs.Any(e =>
            string.Equals(e.ParentTableDbName, table.DbName, StringComparison.OrdinalIgnoreCase));
        if (!isParent)
            return;

        var keyColumns = table.KeyColumns.ToArray();
        if (keyColumns.Length != 1)
            return;

        result.Add(new ComputedColumnDefinition(
            EavMetaProvider.FieldName,
            EavMetaProvider.FieldType,
            ComputedColumnKind.Provider,
            EavMetaProvider.ProviderName,
            new[] { keyColumns[0].DbName }));
    }

    /// <summary>
    /// Emits the read-only <c>_availableTransitions</c> provider column on tables
    /// that carry state-machine metadata. Tables without it are unaffected.
    /// </summary>
    private static void AddStateMachineTransitions(IDbTable table, List<ComputedColumnDefinition> result)
    {
        var definition = StateMachineConfigCollector.FromTable(table);
        if (definition is null)
            return;

        result.Add(new ComputedColumnDefinition(
            StateMachineTransitionsProvider.FieldName,
            StateMachineTransitionsProvider.FieldType,
            ComputedColumnKind.Provider,
            StateMachineTransitionsProvider.ProviderName,
            new[] { definition.StateColumn }));
    }

    public static ComputedColumnDefinition? Find(IDbTable table, string graphQlName)
        => Find(table, graphQlName, null);

    public static ComputedColumnDefinition? Find(IDbTable table, string graphQlName, IDbModel? model)
        => FromTable(table, model).FirstOrDefault(c => string.Equals(c.Name, graphQlName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ComputedColumnDefinition> ParseSql(string? raw)
    {
        foreach (var entry in SplitEntries(raw))
        {
            var parts = entry.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                continue;

            var name = parts[0];
            var type = parts[1];
            var expression = parts[2];
            if (!ComputedColumnDefinition.IsValidGraphQlName(name) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(expression))
                continue;

            // CS0618: parsing the deprecated 'computed-sql' metadata into its Sql-kind definition
            // is the legitimate producer of the admin-gated raw-SQL path; suppress the deprecation
            // warning at this intentional reference.
#pragma warning disable CS0618
            yield return new ComputedColumnDefinition(
                name,
                type,
                ComputedColumnKind.Sql,
                expression,
                ExtractPlaceholders(expression));
#pragma warning restore CS0618
        }
    }

    private static IEnumerable<ComputedColumnDefinition> ParseExpression(string? raw)
    {
        foreach (var entry in SplitEntries(raw))
        {
            var parts = entry.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                continue;

            var name = parts[0];
            var type = parts[1];
            var exprText = parts[2];
            if (!ComputedColumnDefinition.IsValidGraphQlName(name) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(exprText))
                continue;

            var (expression, dependencies) = ComputedExpressionParser.Parse(exprText);
            yield return new ComputedColumnDefinition(
                name,
                type,
                ComputedColumnKind.Expression,
                exprText,
                dependencies)
            {
                Expression = expression,
            };
        }
    }

    private static IEnumerable<ComputedColumnDefinition> ParseProvider(string? raw)
    {
        foreach (var entry in SplitEntries(raw))
        {
            var parts = entry.Split(':', 4, StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                continue;

            var name = parts[0];
            var type = parts[1];
            var provider = parts[2];
            if (!ComputedColumnDefinition.IsValidGraphQlName(name)
                || string.IsNullOrWhiteSpace(type)
                || string.IsNullOrWhiteSpace(provider))
                continue;

            var dependencies = parts.Length == 4 && parts[3].StartsWith("depends=", StringComparison.OrdinalIgnoreCase)
                ? SplitList(parts[3]["depends=".Length..])
                : Array.Empty<string>();

            yield return new ComputedColumnDefinition(
                name,
                type,
                ComputedColumnKind.Provider,
                provider,
                dependencies);
        }
    }

    private static IReadOnlyList<string> ExtractPlaceholders(string expression)
        => System.Text.RegularExpressions.Regex.Matches(expression, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SplitList(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> SplitEntries(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Recursive-descent parser for the <see cref="MetadataKeys.Computed.Expression"/> grammar
    /// into an immutable <see cref="SqlExpr"/> tree. The grammar is deliberately small — column
    /// references, string/numeric literals, allow-listed function calls, and <c>||</c>
    /// concatenation — because the whole point of the Expression kind is that the author cannot
    /// express verbatim SQL: literals become <see cref="SqlExpr.Lit"/> (bound parameters at
    /// lowering) and bare identifiers become <see cref="SqlExpr.Col"/> (schema-validated). The
    /// closed function allow-list is enforced once, at lowering
    /// (<c>SqlDialectBase.MapFunctionName</c>), so it is not duplicated here. Syntax errors
    /// fail fast with <see cref="BifrostExecutionError"/> at config-collection time.
    /// </summary>
    private static class ComputedExpressionParser
    {
        public static (SqlExpr Expression, IReadOnlyList<string> Dependencies) Parse(string text)
        {
            var tokens = Tokenize(text);
            var position = 0;
            var dependencies = new List<string>();
            var expression = ParseConcat(tokens, ref position, dependencies);
            if (position != tokens.Count)
                throw new BifrostExecutionError(
                    $"Computed expression '{text}' has unexpected trailing input near token '{tokens[position].Text}'.");

            var distinct = dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return (expression, distinct);
        }

        private static SqlExpr ParseConcat(IReadOnlyList<Token> tokens, ref int position, List<string> dependencies)
        {
            var parts = new List<SqlExpr> { ParsePrimary(tokens, ref position, dependencies) };
            while (position < tokens.Count && tokens[position].Kind == TokenKind.Concat)
            {
                position++;
                parts.Add(ParsePrimary(tokens, ref position, dependencies));
            }
            return parts.Count == 1 ? parts[0] : new SqlExpr.Concat(parts);
        }

        private static SqlExpr ParsePrimary(IReadOnlyList<Token> tokens, ref int position, List<string> dependencies)
        {
            if (position >= tokens.Count)
                throw new BifrostExecutionError("Computed expression ended unexpectedly; an operand was required.");

            var token = tokens[position];
            switch (token.Kind)
            {
                case TokenKind.String:
                    position++;
                    return new SqlExpr.Lit(token.Text);

                case TokenKind.Number:
                    position++;
                    return new SqlExpr.Lit(ParseNumber(token.Text));

                case TokenKind.LParen:
                    position++;
                    var inner = ParseConcat(tokens, ref position, dependencies);
                    Expect(tokens, ref position, TokenKind.RParen);
                    return inner;

                case TokenKind.Identifier:
                    position++;
                    // A '(' immediately after the identifier makes it a function call; otherwise
                    // it is a column reference (and therefore a declared dependency).
                    if (position < tokens.Count && tokens[position].Kind == TokenKind.LParen)
                    {
                        position++;
                        var args = new List<SqlExpr>();
                        if (position < tokens.Count && tokens[position].Kind != TokenKind.RParen)
                        {
                            args.Add(ParseConcat(tokens, ref position, dependencies));
                            while (position < tokens.Count && tokens[position].Kind == TokenKind.Comma)
                            {
                                position++;
                                args.Add(ParseConcat(tokens, ref position, dependencies));
                            }
                        }
                        Expect(tokens, ref position, TokenKind.RParen);
                        return new SqlExpr.Fn(token.Text.ToUpperInvariant(), args);
                    }

                    dependencies.Add(token.Text);
                    return new SqlExpr.Col(token.Text);

                default:
                    throw new BifrostExecutionError(
                        $"Computed expression has an unexpected token '{token.Text}' where an operand was required.");
            }
        }

        private static object ParseNumber(string text)
        {
            try
            {
                return text.Contains('.', StringComparison.Ordinal)
                    ? decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
                    : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new BifrostExecutionError($"Computed expression has an invalid numeric literal '{text}'.");
            }
        }

        private static void Expect(IReadOnlyList<Token> tokens, ref int position, TokenKind kind)
        {
            if (position >= tokens.Count || tokens[position].Kind != kind)
                throw new BifrostExecutionError($"Computed expression is missing an expected '{kind}' token.");
            position++;
        }

        private enum TokenKind { Identifier, String, Number, LParen, RParen, Comma, Concat }

        private readonly record struct Token(TokenKind Kind, string Text);

        private static IReadOnlyList<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '\'')
                {
                    // Single-quoted string; a doubled '' is an escaped quote.
                    i++;
                    var sb = new System.Text.StringBuilder();
                    while (i < text.Length)
                    {
                        if (text[i] == '\'')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '\'')
                            {
                                sb.Append('\'');
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        sb.Append(text[i]);
                        i++;
                    }
                    tokens.Add(new Token(TokenKind.String, sb.ToString()));
                }
                else if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(")); i++; }
                else if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")")); i++; }
                else if (c == ',') { tokens.Add(new Token(TokenKind.Comma, ",")); i++; }
                else if (c == '|' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    tokens.Add(new Token(TokenKind.Concat, "||"));
                    i += 2;
                }
                else if (char.IsDigit(c))
                {
                    var start = i;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                        i++;
                    tokens.Add(new Token(TokenKind.Number, text[start..i]));
                }
                else if (c == '_' || char.IsLetter(c))
                {
                    var start = i;
                    while (i < text.Length && (text[i] == '_' || char.IsLetterOrDigit(text[i])))
                        i++;
                    tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
                }
                else
                {
                    throw new BifrostExecutionError($"Computed expression contains an unsupported character '{c}'.");
                }
            }
            return tokens;
        }
    }
}
