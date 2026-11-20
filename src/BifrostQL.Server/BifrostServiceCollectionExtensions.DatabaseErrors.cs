using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    public static partial class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <typeparamref name="T"/> as an <see cref="IDatabaseErrorMapper"/>: a constraint
        /// violation a GraphQL field fails on is answered with the error the mapper returns (its own
        /// code and sentence), or BifrostQL's answer when it returns null. Several mappers are asked
        /// in registration order. A singleton: a mapper reads only the violation it is handed.
        /// </summary>
        public static IServiceCollection AddBifrostDatabaseErrorMapper<T>(this IServiceCollection services)
            where T : class, IDatabaseErrorMapper
        {
            services.AddSingleton<IDatabaseErrorMapper, T>();
            return services;
        }
    }
}
