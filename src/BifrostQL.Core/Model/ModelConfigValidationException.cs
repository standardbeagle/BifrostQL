using System;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Thrown when the model's metadata configuration fails validation at model load: a rule
    /// names a column that does not exist, a key has an invalid value, and so on. The message
    /// lists every problem found. Derives from <see cref="InvalidOperationException"/> so callers
    /// that catch the broader type are unaffected; catch this type to tell a configuration fault
    /// (which <c>bifrost config-validate</c> reports) from any other failed model build, such as a
    /// provider with no registered factory or a connection-pool timeout.
    /// </summary>
    public sealed class ModelConfigValidationException : InvalidOperationException
    {
        public ModelConfigValidationException(string message)
            : base(message)
        {
        }
    }
}
