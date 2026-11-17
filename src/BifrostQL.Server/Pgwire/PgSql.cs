using System.Runtime.ExceptionServices;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// One statement's SQL text plus its subset parse, computed at most once. The catalog
    /// responder and the translator both need the parse of an ordinary simple query, and a
    /// prepared statement is translated on every Describe, Bind and Execute; carrying the text
    /// in this type lets every consumer share the first parse. A text that does not parse
    /// rethrows the same <see cref="PgQueryTranslationException"/> on every access.
    /// </summary>
    internal sealed class PgSql
    {
        private PgSelectStatement? _statement;
        private ExceptionDispatchInfo? _parseFailure;

        public PgSql(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

        public string Text { get; }

        /// <summary>The subset parse of <see cref="Text"/>; throws when the text does not parse.</summary>
        public PgSelectStatement Statement
        {
            get
            {
                if (_statement is not null) return _statement;
                _parseFailure?.Throw();
                try
                {
                    return _statement = PgSqlSubsetParser.Parse(Text);
                }
                catch (PgQueryTranslationException ex)
                {
                    _parseFailure = ExceptionDispatchInfo.Capture(ex);
                    throw;
                }
            }
        }

        public static implicit operator PgSql(string text) => new(text);

        public override string ToString() => Text;
    }
}
