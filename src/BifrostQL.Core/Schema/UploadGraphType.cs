using GraphQL.Types;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// The <c>Upload</c> scalar that <c>_fileUpload(file: Upload!)</c> declares once any table or
    /// column carries file-storage metadata. Without a registered type the schema could not
    /// initialize, and storage metadata broke the whole endpoint, not only file upload.
    ///
    /// <para>The value is the file's bytes, which is what <c>FileUploadResolver</c> reads: a
    /// base64 string from a JSON request (inline literal or variable), or bytes a transport
    /// already decoded. Anything else is refused, never coerced; a value that is not valid
    /// base64 fails the request rather than storing garbage.</para>
    /// </summary>
    public sealed class UploadGraphType : ScalarGraphType
    {
        public UploadGraphType()
        {
            Name = "Upload";
            Description = "File content: a base64-encoded string in JSON requests.";
        }

        public override object? ParseValue(object? value) => value switch
        {
            null => null,
            byte[] bytes => bytes,
            string base64 => Convert.FromBase64String(base64),
            _ => throw new FormatException("An Upload value must be a base64-encoded string."),
        };

        public override object? ParseLiteral(GraphQLValue value) => value switch
        {
            GraphQLNullValue => null,
            GraphQLStringValue text => Convert.FromBase64String(text.Value.ToString()),
            _ => throw new FormatException("An Upload literal must be a base64-encoded string."),
        };

        // An input-only scalar: no field returns it, so serialization is never exercised; bytes
        // round-trip as base64 for completeness.
        public override object? Serialize(object? value) => value switch
        {
            null => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => throw new FormatException("An Upload value must be bytes."),
        };
    }
}
