# Query Validation in GraphQL .NET

This document outlines the query validation capabilities in GraphQL .NET, including built-in rules, custom validation, and best practices.

## Built-in Validation Rules

GraphQL .NET includes a number of built-in validation rules that are enabled by default, following the [GraphQL specification](https://spec.graphql.org/October2021/#sec-Validation).

## Adding Custom Validation Rules

Custom validation rules can be added in two ways:

### 1. Using Dependency Injection

```csharp
services.AddGraphQL(b => b
  .AddSchema<MySchema>()
  .AddSystemTextJson()
  .AddValidationRule<RequiresAuthValidationRule>());
```

### 2. Using ExecutionOptions

```csharp
await schema.ExecuteAsync(_ =>
{
  _.Query = "...";
  _.ValidationRules =
    new[]
    {
      new RequiresAuthValidationRule()
    }
    .Concat(DocumentValidator.CoreRules);
});
```

## Input Validation

### Field-Level Validation

You can add validation rules to input arguments or input object fields using the `Validate` method:

```csharp
Field(x => x.FirstName)
    .Validate(value =>
    {
        if (((string)value).Length >= 10)
            throw new ArgumentException("Length must be less than 10 characters.");
    });

Field(x => x.Age)
    .Validate(value =>
    {
        if ((int)value < 18)
            throw new ArgumentException("Age must be 18 or older.");
    });
```

### Custom Validation Attributes

For type-first schemas, you can create custom validation attributes:

```csharp
public class MyMaxLength : GraphQLAttribute
{
    private readonly int _maxLength;
    public MyMaxLength(int maxLength)
    {
        _maxLength = maxLength;
    }

    public override void Modify(ArgumentInformation argumentInformation)
    {
        if (argumentInformation.TypeInformation.Type != typeof(string))
        {
            throw new InvalidOperationException("MyMaxLength can only be used on string arguments.");
        }
    }

    public override void Modify(QueryArgument queryArgument)
    {
        queryArgument.Validate(value =>
        {
            if (((string)value).Length > _maxLength)
            {
                throw new ArgumentException($"Value is too long. Max length is {_maxLength}.");
            }
        });
    }
}
```

## Implementing Custom Validation Rules

### Example: Disabling Introspection

```csharp
public class NoIntrospectionValidationRule : ValidationRuleBase
{
    private static readonly MatchingNodeVisitor<GraphQLField> _visitor = new(
        (field, context) =>
        {
            if (field.Name.Value == "__schema" || field.Name.Value == "__type")
                context.ReportError(new NoIntrospectionError(context.Document.Source, field));
        });

    public override ValueTask<INodeVisitor?> GetPreNodeVisitorAsync(ValidationContext context) => new(_visitor);
}
```

### Example: Connection Size Limits

```csharp
public class NoConnectionOver1000ValidationRule : ValidationRuleBase, IVariableVisitorProvider, INodeVisitor
{
    public override ValueTask<INodeVisitor?> GetPostNodeVisitorAsync(ValidationContext context)
        => context.ArgumentValues != null ? new(this) : default;

    ValueTask INodeVisitor.EnterAsync(ASTNode node, ValidationContext context)
    {
        if (node is not GraphQLField fieldNode)
            return default;

        var fieldDef = context.TypeInfo.GetFieldDef();
        if (fieldDef == null || fieldDef.ResolvedType?.GetNamedType() is not IObjectGraphType connectionType || !connectionType.Name.EndsWith("Connection"))
            return default;

        if (!(context.ArgumentValues?.TryGetValue(fieldNode, out var args) ?? false))
            return default;

        ArgumentValue lastArg = default;
        if (!args.TryGetValue("first", out var firstArg) && !args.TryGetValue("last", out lastArg))
            return default;

        var rows = (int?)firstArg.Value ?? (int?)lastArg.Value ?? 0;
        if (rows > 1000)
            context.ReportError(new ValidationError("Cannot return more than 1000 rows"));

        return default;
    }

    ValueTask INodeVisitor.LeaveAsync(ASTNode node, ValidationContext context) => default;
}
```

## Schema-Level Validation

You can add validation rules at the schema level using a schema node visitor:

```csharp
services.AddGraphQL(b => b
    .AddSchema<MySchema>()
    .AddSchemaVisitor<NoConnectionOver1000Visitor>());
    
public class NoConnectionOver1000Visitor : BaseSchemaNodeVisitor
{
    public override void VisitObjectFieldArgumentDefinition(QueryArgument argument, FieldType field, IObjectGraphType type, ISchema schema)
        => argument.Validator += GetValidator(argument, field);

    public override void VisitInterfaceFieldArgumentDefinition(QueryArgument argument, FieldType field, IInterfaceGraphType type, ISchema schema)
        => field.Validator += GetValidator(argument, field);

    private static Action<object?>? GetValidator(QueryArgument argument, FieldType field)
    {
        if (!field.ResolvedType!.GetNamedType().Name.EndsWith("Connection"))
            return null;

        if (argument.Name != "first" && argument.Name != "last")
            return null;

        return value =>
        {
            if (value is int intValue && intValue > 1000)
                throw new ArgumentException("Cannot return more than 1000 rows.");
        };
    }
}
```

## Best Practices

1. Use field-level validation for simple input validation (e.g., string length, numeric ranges)
2. Use custom validation rules for complex validation logic that spans multiple fields or requires context
3. Consider using schema visitors for validation rules that apply across the entire schema
4. Prefer the `Validate` method over custom validation rules when possible for better performance
5. Always provide clear error messages that help clients understand and fix validation issues
6. Consider implementing validation rules that align with your business rules and security requirements

## Error Handling

Validation errors are returned in the GraphQL response format:

```json
{
  "errors": [
    {
      "message": "Invalid value for argument 'firstName' of field 'testMe'. Length must be less than 10 characters.",
      "locations": [
        {
          "line": 1,
          "column": 14
        }
      ],
      "extensions": {
        "code": "INVALID_VALUE",
        "codes": [
          "INVALID_VALUE",
          "ARGUMENT"
        ],
        "number": "5.6"
      }
    }
  ]
}
```

For custom error messages, throw a `ValidationError` or derived exception class.