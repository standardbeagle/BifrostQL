namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Strategy interface for discovering and establishing relationships between tables.
    /// </summary>
    public interface ITableRelationshipStrategy
    {
        /// <summary>
        /// Discovers and establishes relationships between tables in the model.
        /// </summary>
        /// <param name="model">The database model containing tables to link.</param>
        /// <param name="foreignKeys">Foreign key constraints from the database schema.</param>
        void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys);
    }
}
