namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Orchestrates the discovery of table relationships using multiple strategies.
    /// Coordinates foreign key-based, name-based, and many-to-many relationship detection.
    /// </summary>
    public sealed class TableRelationshipOrchestrator
    {
        private readonly ForeignKeyRelationshipStrategy _fkStrategy;
        private readonly ManyToManyDetectionStrategy _m2mStrategy;

        public TableRelationshipOrchestrator(
            ForeignKeyRelationshipStrategy? fkStrategy = null,
            ManyToManyDetectionStrategy? m2mStrategy = null)
        {
            _fkStrategy = fkStrategy ?? new ForeignKeyRelationshipStrategy();
            _m2mStrategy = m2mStrategy ?? new ManyToManyDetectionStrategy();
        }

        /// <summary>
        /// Discovers all relationships in the model using all strategies.
        /// </summary>
        /// <param name="model">The database model.</param>
        /// <param name="foreignKeys">Foreign key constraints.</param>
        /// <param name="prefixGroups">Prefix groups for name-based matching.</param>
        public void LinkTables(
            IDbModel model, 
            IReadOnlyCollection<DbForeignKey> foreignKeys,
            IReadOnlyList<PrefixGroup> prefixGroups)
        {
            // Step 1: Foreign key relationships (most reliable)
            _fkStrategy.DiscoverRelationships(model, foreignKeys);

            // Step 2: Name-based relationships (for convention-based naming)
            var nameStrategy = new NameBasedRelationshipStrategy(prefixGroups);
            nameStrategy.DiscoverRelationships(model, foreignKeys);

            // Step 3: Many-to-many from metadata
            _m2mStrategy.DetectFromMetadata(model);

            // Step 4: Auto-detect many-to-many from foreign keys
            _m2mStrategy.AutoDetect(model, foreignKeys);
        }
    }
}
