namespace BifrostQL.Core.Operations;

/// <summary>Which root type an operation's field is emitted on.</summary>
public enum BifrostOperationKind
{
    /// <summary>A field on the root query type; reads only, no shared transaction.</summary>
    Query,

    /// <summary>
    /// A field on the root mutation type. Every read and write the operation makes through
    /// its <see cref="BifrostOperationContext"/> shares one transaction that commits when the
    /// operation returns and rolls back when it throws.
    /// </summary>
    Mutation,
}

/// <summary>
/// An application-declared server operation: a root GraphQL field whose resolver is
/// application code, running with the same security as the generated fields.
///
/// <para>Register it with <c>AddBifrostQL(o =&gt; o.AddOperation&lt;T&gt;())</c> and expose it
/// per endpoint with the <c>:root { operations: name[grant,list] }</c> metadata key; an
/// operation that is registered but not listed is not in the schema. Before
/// <see cref="ExecuteAsync"/> runs, the resolver refuses a caller who holds none of the
/// listed grants, or who has no tenant claim when <c>operations-tenant</c> is required.</para>
///
/// <para>Data access goes through <see cref="BifrostOperationContext"/>, which always acts as the
/// calling user: tenant isolation, policy, soft delete, audit stamping and the mutation hooks
/// apply exactly as they do for generated fields.</para>
/// </summary>
public interface IBifrostOperation
{
    /// <summary>The root field name. Must be a GraphQL name that no generated field uses.</summary>
    string Name { get; }

    /// <summary>The root type the field is emitted on.</summary>
    BifrostOperationKind Kind { get; }

    /// <summary>
    /// The field's SDL after its name: optional arguments, then the return type, for example
    /// <c>(lineId: Int!): Float</c> or <c>: PriceSummary!</c>.
    /// </summary>
    string Signature { get; }

    /// <summary>
    /// Extra SDL definitions (types, inputs, enums) that <see cref="Signature"/> references.
    /// Fields of an object type resolve from the returned object's dictionary keys or properties.
    /// </summary>
    string TypeDefinitions => string.Empty;

    /// <summary>Runs the operation and returns the field's value.</summary>
    ValueTask<object?> ExecuteAsync(BifrostOperationContext context);
}
