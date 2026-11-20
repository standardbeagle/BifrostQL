using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The one place a host's <see cref="IDatabaseErrorMapper"/>s answer a constraint violation.
    /// Called where a GraphQL field's <see cref="BifrostExecutionError"/> becomes the client's error
    /// (<see cref="BifrostDispatcher"/>), so a generated write, a <c>_batch</c>, a tree write and a
    /// server operation are answered alike.
    /// </summary>
    internal static class DatabaseErrorMapping
    {
        /// <summary>
        /// The first non-null answer of the registered mappers for <paramref name="error"/>'s
        /// <see cref="BifrostExecutionError.ConstraintViolation"/>, or <paramref name="error"/>
        /// itself when it reports none, no mapper is registered, or every mapper returns null.
        /// A mapper that throws fails the field: its exception propagates.
        /// </summary>
        public static BifrostExecutionError Apply(BifrostExecutionError error, IServiceProvider? services)
        {
            if (error.ConstraintViolation is not { } violation || services is null)
                return error;
            foreach (var mapper in services.GetServices<IDatabaseErrorMapper>())
            {
                if (mapper.Map(violation) is { } mapped)
                    return mapped;
            }
            return error;
        }
    }
}
