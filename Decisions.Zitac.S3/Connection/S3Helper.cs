using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Steps;

namespace Decisions.Zitac.S3.Connection
{
    public static class S3Helper
    {
        public const string METADATA_HEADER_PREFIX = "x-amz-meta-";

        /// <summary>
        /// Unwraps async wrappers and turns S3 errors into a readable message. When the server says it doesn't
        /// implement something, says so plainly: with an unknown S3 product that is the most common failure.
        /// </summary>
        public static string DescribeException(Exception ex, string feature = null, ResolvedConnection connection = null)
        {
            // An anonymous request (no access key) is refused with a plain AccessDenied; say why instead.
            if (connection?.IsAnonymous == true && FindInner<AmazonServiceException>(ex) is { ErrorCode: "AccessDenied" })
                return "Access denied: no access key was given, so the request was sent anonymously. Set Credentials > Access Key ID and Secret Access Key (check that the Credentials input is mapped and the values aren't empty).";

            while ((ex is AggregateException || ex is System.Reflection.TargetInvocationException) && ex.InnerException != null)
                ex = ex.InnerException;

            if (ex is AmazonServiceException ase)
            {
                if (IsNotSupported(ase))
                    return $"This S3 server does not support {feature ?? "this operation"} ({ase.ErrorCode ?? ((int)ase.StatusCode).ToString()}).";

                var details = new List<string>();
                if (!string.IsNullOrEmpty(ase.ErrorCode)) details.Add($"ErrorCode: {ase.ErrorCode}");
                if (ase.StatusCode != 0) details.Add($"HTTP {(int)ase.StatusCode} {ase.StatusCode}");
                if (!string.IsNullOrEmpty(ase.RequestId)) details.Add($"RequestId: {ase.RequestId}");
                // Some servers (e.g. Ceph) send no message text, only the error code.
                string text = string.IsNullOrWhiteSpace(ase.Message)
                    ? $"The S3 server returned {(string.IsNullOrEmpty(ase.ErrorCode) ? "an error" : ase.ErrorCode)}."
                    : ase.Message.Trim();
                string message = details.Count == 0 ? text : $"{text} ({string.Join(", ", details)})";

                string hint = HintFor(ase);
                return hint == null ? message : $"{message} {hint}";
            }

            // Hints name the fields as designers see them (Connection / Credentials inputs).
            if (FindInner<System.Security.Authentication.AuthenticationException>(ex) is { } tls)
                return $"Secure connection (TLS) failed: {tls.Message} Ask IT to trust the S3 server's certificate on the Decisions server, or, as a last resort, turn on the Accept Untrusted Certificate connection setting.";

            if (ex is HttpRequestException || FindInner<System.Net.Sockets.SocketException>(ex) != null)
                return $"Could not reach the S3 server: {ex.Message} Check Credentials > Server URL (address, port, http/https) and that the Decisions server can reach it through the network/firewall.";

            if (ex is TaskCanceledException || ex is TimeoutException)
                return $"The S3 server did not answer in time. Check Credentials > Server URL, or raise the Timeout Seconds connection setting for large files.";

            return ex.Message;
        }

        private const string SignatureHint = "Check Credentials > Secret Access Key. If it is correct, ask the S3 administrator which region to use and set Credentials > Region.";

        private static string HintFor(AmazonServiceException ex)
        {
            switch (ex.ErrorCode)
            {
                case "InvalidAccessKeyId":
                    return "Check Credentials > Access Key ID.";
                case "SignatureDoesNotMatch":
                    return SignatureHint;
                case "AuthorizationHeaderMalformed":
                case "PermanentRedirect":
                case "IllegalLocationConstraintException":
                    return "Wrong region: set Credentials > Region to the bucket's region (ask the S3 administrator).";
                // Some servers (e.g. Garage) report a wrong secret or region as AccessDenied "Invalid signature".
                case "AccessDenied" when ex.Message?.Contains("signature", StringComparison.OrdinalIgnoreCase) == true:
                    return SignatureHint;
                case "AccessDenied":
                    return "These credentials are not allowed to do this. Ask the S3 administrator for access to the bucket (and check Credentials > Access Key ID).";
                case "InvalidTag":
                    return "Tag keys and values may only contain letters, numbers, spaces and + - = . _ : / @. Some S3 servers (e.g. MinIO) don't accept å, ä, ö or other non-ASCII letters in tags; store such values as metadata instead.";
                case "NoSuchBucket":
                    return "The bucket was not found. Check the bucket name (case-sensitive). If it is right: use exactly the server address the S3 administrator gave you in Credentials > Server URL (some servers, e.g. Ceph, mistake an unknown host name for a bucket name), or ask whether the server needs the Use Virtual Hosted Style connection setting.";
                case "NoSuchKey":
                // HEAD requests (Get File Info, Copy's metadata read) have no error body, so servers report just "NotFound".
                case "NotFound":
                    return "The file does not exist. Check the key (path in the bucket); keys are case-sensitive.";
                case "MetadataTooLarge":
                case "RequestHeaderSectionTooLarge":
                    return "The server limits total metadata size (2 KB on AWS); encoded non-ASCII values take more space than they look.";
                case "XAmzContentSHA256Mismatch":
                case "InvalidDigest":
                case "BadDigest":
                case "InvalidArgument" when ex.Message?.Contains("checksum", StringComparison.OrdinalIgnoreCase) == true:
                    return "The server does not accept the AWS checksums. Make sure the Use AWS Checksums connection setting is off.";
                case "RequestTimeTooSkewed":
                    return "The clocks of the Decisions server and the S3 server differ too much. Ask IT to check time sync (NTP).";
                // e.g. Zenko/Scality CloudServer when the host name isn't in its restEndpoints list.
                case "InvalidURI":
                    return "The S3 server doesn't recognize the host name in Credentials > Server URL. Use exactly the server address the S3 administrator gave you (servers often accept only their configured names, not aliases or IP addresses).";
                default:
                    return null;
            }
        }

        private static T FindInner<T>(Exception ex) where T : Exception
        {
            for (Exception e = ex; e != null; e = e.InnerException)
                if (e is T t)
                    return t;
            return null;
        }

        public static bool IsNotSupported(AmazonServiceException ex)
        {
            return ex.StatusCode == HttpStatusCode.NotImplemented
                || ex.ErrorCode == "NotImplemented"
                || ex.ErrorCode == "NotSupported"
                || ex.ErrorCode == "UnsupportedOperation";
        }

        public static void RequireBucket(string bucket)
        {
            if (string.IsNullOrWhiteSpace(bucket))
                throw new ArgumentException("Bucket is required");
        }

        /// <summary>Normalizes an object key: no leading slash, forward slashes only.</summary>
        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key (file path in the bucket) is required");
            return key.Trim().Replace('\\', '/').TrimStart('/');
        }

        /// <summary>Joins a folder and a file name into a key ("a/b" + "c.pdf" = "a/b/c.pdf").</summary>
        public static string CombineKey(string folder, string fileName)
        {
            string f = (folder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            string n = NormalizeKey(fileName);
            return f.Length == 0 ? n : $"{f}/{n}";
        }

        public static string FileNameFromKey(string key)
        {
            int i = key.LastIndexOf('/');
            return i < 0 ? key : key.Substring(i + 1);
        }

        /// <summary>
        /// User metadata travels as HTTP headers, which are only reliably ASCII, so values are encoded per the
        /// connection's Metadata Encoding (RFC 2047 by default). Keys can't be encoded (header names) and stay ASCII tokens.
        /// </summary>
        public static void AddMetadata(MetadataCollection target, S3KeyValue[] metadata, MetadataEncoding encoding)
        {
            if (metadata == null)
                return;
            foreach (S3KeyValue item in metadata)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Key))
                    continue;
                string key = item.Key.Trim();
                if (key.StartsWith(METADATA_HEADER_PREFIX, StringComparison.OrdinalIgnoreCase))
                    key = key.Substring(METADATA_HEADER_PREFIX.Length);
                if (!IsAsciiToken(key))
                    throw new ArgumentException($"Metadata key '{item.Key}' may only contain letters a-z, digits, '-' and '_'");
                // S3 stores metadata keys lowercase.
                target.Add(key.ToLowerInvariant(), MetadataCodec.Encode(item.Value, encoding, key));
            }
        }

        public static S3KeyValue[] ReadMetadata(MetadataCollection metadata, MetadataEncoding encoding)
        {
            if (metadata == null)
                return Array.Empty<S3KeyValue>();
            return metadata.Keys
                .Select(k => new S3KeyValue
                {
                    Key = k.StartsWith(METADATA_HEADER_PREFIX, StringComparison.OrdinalIgnoreCase) ? k.Substring(METADATA_HEADER_PREFIX.Length) : k,
                    Value = MetadataCodec.Decode(metadata[k], encoding)
                })
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToArray();
        }

        public static List<Tag> ToTags(S3KeyValue[] tags)
        {
            if (tags == null)
                return new List<Tag>();
            List<Tag> result = tags
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Key))
                .Select(t => new Tag { Key = t.Key.Trim(), Value = t.Value ?? string.Empty })
                .ToList();
            // S3's limit; most compatible servers use the same one.
            if (result.Count > 10)
                throw new ArgumentException("S3 allows at most 10 tags per file");
            return result;
        }

        public static S3KeyValue[] FromTags(IEnumerable<Tag> tags)
        {
            return (tags ?? Enumerable.Empty<Tag>())
                .Select(t => new S3KeyValue { Key = t.Key, Value = t.Value })
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsAsciiToken(string s)
        {
            return s.Length > 0 && s.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_');
        }
    }
}
