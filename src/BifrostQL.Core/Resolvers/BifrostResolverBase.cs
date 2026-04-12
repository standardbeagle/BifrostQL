using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Abstract base class for Bifrost resolvers that implement both <see cref="IBifrostResolver"/>
    /// and <see cref="IFieldResolver"/>.
    /// </summary>
    /// <remarks>
    /// This base class eliminates the boilerplate code needed to adapt GraphQL.NET's 
    /// <see cref="IFieldResolver"/> to Bifrost's <see cref="IBifrostResolver"/> interface.
    /// 
    /// Derived classes only need to implement <see cref="ResolveAsync(IBifrostFieldContext)"/>.
    /// The <see cref="IFieldResolver.ResolveAsync(IResolveFieldContext)"/> implementation
    /// is provided automatically by this base class.
    /// </remarks>
    public abstract class BifrostResolverBase : IBifrostResolver, IFieldResolver
    {
        /// <summary>
        /// Resolves the field value using the Bifrost field context.
        /// </summary>
        /// <param name="context">The Bifrost field context containing resolver information.</param>
        /// <returns>The resolved value.</returns>
        public abstract ValueTask<object?> ResolveAsync(IBifrostFieldContext context);

        /// <summary>
        /// Adapts the GraphQL.NET field context to Bifrost's context and delegates to 
        /// <see cref="ResolveAsync(IBifrostFieldContext)"/>.
        /// </summary>
        /// <param name="context">The GraphQL.NET resolve field context.</param>
        /// <returns>The resolved value.</returns>
        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }
    }
}
