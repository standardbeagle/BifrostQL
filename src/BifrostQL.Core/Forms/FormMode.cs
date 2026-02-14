namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Specifies the mode of an HTML form generated from a database table.
    /// </summary>
    public enum FormMode
    {
        /// <summary>Create a new record. IDENTITY columns are excluded.</summary>
        Insert,

        /// <summary>Edit an existing record. Values are pre-populated and the primary key is a hidden field.</summary>
        Update,

        /// <summary>Confirm deletion. Fields are rendered read-only with a confirmation prompt.</summary>
        Delete,
    }
}
