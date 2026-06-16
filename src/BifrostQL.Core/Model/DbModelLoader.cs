using BifrostQL.Core.Model;
using Microsoft.Extensions.Configuration;
using Pluralize.NET.Core;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

namespace BifrostQL.Model
{
    public sealed class DbModelLoader
    {
        private readonly IDbConnFactory _connFactory;
        private readonly IMetadataLoader _metadataLoader;

        // Backward compatibility constructor
        public DbModelLoader(string connectionString, IMetadataLoader metadataLoader)
            : this(new DbConnFactory(connectionString), metadataLoader)
        {
        }

        public DbModelLoader(IDbConnFactory connFactory, IMetadataLoader metadataLoader)
        {
            _connFactory = connFactory;
            _metadataLoader = metadataLoader;
        }

        public Task<IDbModel> LoadAsync()
        {
            return LoadAsync(null);
        }

        public async Task<IDbModel> LoadAsync(IDictionary<string, IDictionary<string, object?>>? additionalMetadata)
        {
            return BuildModel(await ReadAsync(), _metadataLoader, additionalMetadata);
        }

        /// <summary>
        /// Loads the DISTINCT values of every <c>enum:</c>-marked lookup table once,
        /// over a single shared connection, so the (async) schema-loader body can
        /// pre-load them and hand the result to the (synchronous) per-profile cache.
        /// </summary>
        public Task<BifrostQL.Core.Schema.EnumValueLoader.LoadResult> LoadEnumValuesAsync(IDbModel model)
            => BifrostQL.Core.Schema.EnumValueLoader.LoadAsync(model, _connFactory);

        /// <summary>
        /// Reads the database schema once. The result can drive many
        /// <see cref="BuildModel"/> calls with different metadata loaders, so
        /// per-profile metadata can vary without re-reading the database.
        /// </summary>
        public async Task<SchemaData> ReadAsync(CancellationToken ct = default)
        {
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync(ct);
            return await _connFactory.SchemaReader.ReadSchemaAsync(conn);
        }

        /// <summary>
        /// Builds a model from a previously-read schema using a caller-supplied
        /// metadata loader. This is the per-profile build step — metadata shapes
        /// the model (visibility hiding, polymorphic links, soft-delete markers)
        /// at build time, while the expensive DB read is shared via
        /// <see cref="ReadAsync"/>.
        /// </summary>
        public IDbModel BuildModel(
            SchemaData read,
            IMetadataLoader metadataLoader,
            IDictionary<string, IDictionary<string, object?>>? additionalMetadata = null)
        {
            // FromTables mutates its input tables in place (applies metadata,
            // populates link dictionaries). Clone the read's tables so every
            // build starts from the pristine schema and produces an independent
            // model — the contract that makes read-once/build-many safe.
            var tables = read.Tables.Cast<DbTable>().Select(CloneForBuild).ToList();

            var model = DbModel.FromTables(
                tables,
                metadataLoader,
                Array.Empty<DbStoredProcedure>(),
                read.ForeignKeys,
                additionalMetadata);
            model.TypeMapper = _connFactory.TypeMapper;

            // Fail-fast: validate stringly-typed metadata configs now that the model is
            // fully built (ColumnLookup + applied metadata available). Aggregates every
            // structural problem into one descriptive exception instead of letting a typo
            // surface only as a runtime BifrostExecutionError on the first query.
            ModelConfigValidator.Validate(model);
            return model;
        }

        /// <summary>
        /// Produces a copy of a read table with fresh per-build mutable state:
        /// a copied metadata dictionary, copied columns (each with its own
        /// metadata), and empty link dictionaries that <see cref="DbModel.FromTables"/>
        /// will repopulate. Immutable identity (names, schema, keys) is shared.
        /// </summary>
        private static DbTable CloneForBuild(DbTable source)
        {
            var columns = source.ColumnLookup.ToDictionary(
                kv => kv.Key,
                kv => CloneColumn(kv.Value),
                StringComparer.OrdinalIgnoreCase);

            return new DbTable
            {
                DbName = source.DbName,
                GraphQlName = source.GraphQlName,
                NormalizedName = source.NormalizedName,
                TableSchema = source.TableSchema,
                TableType = source.TableType,
                Metadata = new Dictionary<string, object?>(source.Metadata),
                ColumnLookup = columns,
                GraphQlLookup = columns.Values.ToDictionary(c => c.GraphQlName, c => c),
                ColumnPrefixGroups = source.ColumnPrefixGroups,
            };
        }

        private static ColumnDto CloneColumn(ColumnDto source)
        {
            return new ColumnDto
            {
                TableCatalog = source.TableCatalog,
                TableSchema = source.TableSchema,
                TableName = source.TableName,
                ColumnName = source.ColumnName,
                GraphQlName = source.GraphQlName,
                NormalizedName = source.NormalizedName,
                DetectedPrefix = source.DetectedPrefix,
                ColumnRef = source.ColumnRef,
                DataType = source.DataType,
                IsNullable = source.IsNullable,
                OrdinalPosition = source.OrdinalPosition,
                IsIdentity = source.IsIdentity,
                IsComputed = source.IsComputed,
                IsPrimaryKey = source.IsPrimaryKey,
                IsUnique = source.IsUnique,
                Metadata = new Dictionary<string, object?>(source.Metadata),
            };
        }
    }
}
