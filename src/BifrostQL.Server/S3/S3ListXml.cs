using System.Globalization;
using System.Text;

namespace BifrostQL.Server.S3
{
    /// <summary>One bucket in a <c>ListAllMyBucketsResult</c>.</summary>
    public sealed record S3BucketInfo(string Name, DateTime CreationDate);

    /// <summary>One object in a <c>ListBucketResult</c> <c>Contents</c> entry.</summary>
    public sealed record S3ObjectInfo(string Key, DateTime LastModified, string? ETag, long Size);

    /// <summary>A single page of a <c>ListObjectsV2</c> enumeration, ready to render.</summary>
    public sealed record S3ListObjectsPage(
        string Bucket,
        string Prefix,
        string? Delimiter,
        int MaxKeys,
        bool IsTruncated,
        IReadOnlyList<S3ObjectInfo> Objects,
        IReadOnlyList<string> CommonPrefixes,
        string? ContinuationToken,
        string? NextContinuationToken,
        string? StartAfter);

    /// <summary>
    /// Renders the S3 list responses in the request/response shapes AWS CLI and rclone
    /// parse: <c>ListAllMyBucketsResult</c> and the ListObjectsV2 <c>ListBucketResult</c>,
    /// both under the canonical <c>http://s3.amazonaws.com/doc/2006-03-01/</c> namespace.
    ///
    /// <para>Every text node is XML-escaped (object keys, prefixes, and the opaque
    /// continuation token can all carry <c>&amp;</c>/<c>&lt;</c>/<c>&gt;</c> once
    /// percent-decoding or base64url is in play), and timestamps use the exact
    /// ISO-8601 millisecond-Z form S3 emits, so a strict client parser accepts the
    /// document.</para>
    /// </summary>
    public static class S3ListXml
    {
        private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
        private const string StorageClass = "STANDARD";

        public static string ListAllMyBuckets(IReadOnlyList<S3BucketInfo> buckets, string ownerId, string ownerName)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<ListAllMyBucketsResult xmlns=\"").Append(Namespace).Append("\">");
            sb.Append("<Owner><ID>").Append(Escape(ownerId)).Append("</ID>")
              .Append("<DisplayName>").Append(Escape(ownerName)).Append("</DisplayName></Owner>");
            sb.Append("<Buckets>");
            foreach (var bucket in buckets)
            {
                sb.Append("<Bucket><Name>").Append(Escape(bucket.Name)).Append("</Name>")
                  .Append("<CreationDate>").Append(Timestamp(bucket.CreationDate)).Append("</CreationDate></Bucket>");
            }
            sb.Append("</Buckets>");
            sb.Append("</ListAllMyBucketsResult>");
            return sb.ToString();
        }

        public static string ListObjectsV2(S3ListObjectsPage page)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<ListBucketResult xmlns=\"").Append(Namespace).Append("\">");
            sb.Append("<Name>").Append(Escape(page.Bucket)).Append("</Name>");
            // Prefix is always present (empty element when none), matching S3.
            sb.Append("<Prefix>").Append(Escape(page.Prefix)).Append("</Prefix>");
            // KeyCount is Keys + CommonPrefixes actually returned in this page.
            sb.Append("<KeyCount>").Append(page.Objects.Count + page.CommonPrefixes.Count).Append("</KeyCount>");
            sb.Append("<MaxKeys>").Append(page.MaxKeys).Append("</MaxKeys>");
            if (page.Delimiter is not null)
                sb.Append("<Delimiter>").Append(Escape(page.Delimiter)).Append("</Delimiter>");
            sb.Append("<IsTruncated>").Append(page.IsTruncated ? "true" : "false").Append("</IsTruncated>");

            if (!string.IsNullOrEmpty(page.ContinuationToken))
                sb.Append("<ContinuationToken>").Append(Escape(page.ContinuationToken)).Append("</ContinuationToken>");
            if (!string.IsNullOrEmpty(page.NextContinuationToken))
                sb.Append("<NextContinuationToken>").Append(Escape(page.NextContinuationToken)).Append("</NextContinuationToken>");
            if (!string.IsNullOrEmpty(page.StartAfter))
                sb.Append("<StartAfter>").Append(Escape(page.StartAfter)).Append("</StartAfter>");

            foreach (var obj in page.Objects)
            {
                sb.Append("<Contents>");
                sb.Append("<Key>").Append(Escape(obj.Key)).Append("</Key>");
                sb.Append("<LastModified>").Append(Timestamp(obj.LastModified)).Append("</LastModified>");
                // An object written before the ETag contract existed has none; S3 always
                // emits an ETag element, so fall back to the empty-content MD5's shape
                // only when present — otherwise omit rather than fabricate a wrong hash.
                if (!string.IsNullOrEmpty(obj.ETag))
                    sb.Append("<ETag>").Append(Escape(Quote(obj.ETag))).Append("</ETag>");
                sb.Append("<Size>").Append(obj.Size.ToString(CultureInfo.InvariantCulture)).Append("</Size>");
                sb.Append("<StorageClass>").Append(StorageClass).Append("</StorageClass>");
                sb.Append("</Contents>");
            }

            foreach (var prefix in page.CommonPrefixes)
                sb.Append("<CommonPrefixes><Prefix>").Append(Escape(prefix)).Append("</Prefix></CommonPrefixes>");

            sb.Append("</ListBucketResult>");
            return sb.ToString();
        }

        /// <summary>
        /// Renders the <c>CopyObjectResult</c> body S3 returns from a successful
        /// CopyObject: the destination object's new validators (ETag = the stored
        /// single-part MD5, and LastModified), under the canonical namespace. The
        /// ETag lives in the body — never a response header — matching S3, so a
        /// client does not confuse it with the source's validator.
        /// </summary>
        public static string CopyObjectResult(string? etag, DateTime lastModified)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<CopyObjectResult xmlns=\"").Append(Namespace).Append("\">");
            sb.Append("<LastModified>").Append(Timestamp(lastModified)).Append("</LastModified>");
            if (!string.IsNullOrEmpty(etag))
                sb.Append("<ETag>").Append(Escape(Quote(etag))).Append("</ETag>");
            sb.Append("</CopyObjectResult>");
            return sb.ToString();
        }

        private static string Quote(string etag)
            => etag.StartsWith('"') ? etag : $"\"{etag}\"";

        private static string Timestamp(DateTime value)
            => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        private static string Escape(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
