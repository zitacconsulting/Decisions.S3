using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Steps;

namespace Decisions.Zitac.S3.Connection
{
    /// <summary>Backs the Test S3 Connection and Check S3 Features steps.</summary>
    public static class S3Diagnostics
    {
        public const string FEATURE_CHECK_FOLDER = "decisions-s3-feature-check/";

        /// <summary>
        /// The HTTP Server header of an unauthenticated request to the server; often names the S3 product
        /// (useful when nobody knows what the S3 storage runs on). Never throws.
        /// </summary>
        public static string GetServerSoftware(ResolvedConnection connection)
        {
            if (connection.IsAmazonS3)
                return "Amazon S3";
            try
            {
                using var handler = new HttpClientHandler();
                if (connection.AcceptUntrustedCertificate)
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                using HttpResponseMessage response = http.GetAsync(connection.EndpointUrl).GetAwaiter().GetResult();
                string server = response.Headers.Server?.ToString();
                return string.IsNullOrWhiteSpace(server) ? "(not reported by the server)" : server;
            }
            catch (Exception ex)
            {
                return "(could not be read: " + S3Helper.DescribeException(ex) + ")";
            }
        }

        /// <summary>Throws (with a hint) when the bucket can't be listed.</summary>
        public static void ListFirstObject(AmazonS3Client client, ResolvedConnection connection, string bucket)
        {
            if (connection.ListApiVersion == ListApiVersion.V1)
                client.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket, MaxKeys = 1 }).GetAwaiter().GetResult();
            else
                client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }).GetAwaiter().GetResult();
        }

        public static string[] ListBuckets(AmazonS3Client client)
        {
            ListBucketsResponse response = client.ListBucketsAsync().GetAwaiter().GetResult();
            return (response.Buckets ?? new List<S3Bucket>()).Select(b => b.BucketName).OrderBy(b => b, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Writes, reads and deletes small temporary files under decisions-s3-feature-check/ in the bucket to find out
        /// which features the server supports. Each check is independent; one failing doesn't stop the others.
        /// </summary>
        public static S3FeatureCheck[] CheckFeatures(AmazonS3Client client, ResolvedConnection connection, string bucket)
        {
            string key = $"{FEATURE_CHECK_FOLDER}{Guid.NewGuid():N}.txt";

            // Basic access first. If the bucket, credentials or server are wrong, every check would fail the same way,
            // so throw: the step then goes to its Error outcome with the hint, like Test S3 Connection.
            // Network errors propagate as they are; only "this list call isn't supported" is left to the checks below.
            try
            {
                ListFirstObject(client, connection, bucket);
            }
            catch (AmazonS3Exception ex) when (S3Helper.IsNotSupported(ex))
            {
                // e.g. the configured List API Version isn't supported: reported per check below.
            }

            var results = new List<S3FeatureCheck>
            {
                Check("List files (V2)", "Needed by Search Files with the default List API Version.", () =>
                    client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }).GetAwaiter().GetResult()),
                Check("List files (V1)", "Only needed if V2 fails; then set the List API Version connection setting to V1.", () =>
                    client.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket, MaxKeys = 1 }).GetAwaiter().GetResult())
            };

            S3FeatureCheck upload = Check("Upload with metadata", "Needed by Upload File.", () =>
            {
                var put = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "Decisions S3 feature check", ContentType = "text/plain" };
                put.Metadata.Add("decisions-check", "yes");
                S3ClientFactory.ApplyCompatibility(put, connection);
                client.PutObjectAsync(put).GetAwaiter().GetResult();
            });
            results.Add(upload);

            if (upload.Supported)
            {
                results.Add(Check("Read metadata", "Needed by Get File Info and the Search Files metadata filter.", () =>
                {
                    GetObjectMetadataResponse head = client.GetObjectMetadataAsync(bucket, key).GetAwaiter().GetResult();
                    if (S3Helper.ReadMetadata(head.Metadata, connection.MetadataEncoding).All(m => m.Key != "decisions-check"))
                        throw new Exception("The file was stored without its metadata.");
                }));
                results.Add(CheckNonAsciiMetadata(client, connection, bucket, key + ".non-ascii"));
                results.Add(Check("Set tags", "Needed by Set File Tags and the Tags input of Upload File.", () =>
                    client.PutObjectTaggingAsync(new PutObjectTaggingRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        Tagging = new Tagging { TagSet = new List<Tag> { new Tag { Key = "decisions-check", Value = "yes" } } }
                    }).GetAwaiter().GetResult(), "object tagging"));
                results.Add(Check("Read tags", "Needed by Get File Tags, Include Tags and the Search Files tag filter.", () =>
                    client.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key }).GetAwaiter().GetResult(), "object tagging"));
                results.Add(Check("Copy file", "Needed by Copy or Move File.", () =>
                {
                    client.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = key, DestinationBucket = bucket, DestinationKey = key + ".copy" }).GetAwaiter().GetResult();
                    client.DeleteObjectAsync(bucket, key + ".copy").GetAwaiter().GetResult();
                }));
                results.Add(Check("Delete file", "Needed by Delete File and Move.", () =>
                    client.DeleteObjectAsync(bucket, key).GetAwaiter().GetResult()));
            }

            results.Add(Check("Bucket versioning", "Information only: whether old versions of files are kept.", () =>
            {
                GetBucketVersioningResponse v = client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }).GetAwaiter().GetResult();
                return "Versioning: " + (v.VersioningConfig?.Status?.Value ?? "Off");
            }, "versioning"));

            return results.ToArray();
        }

        public static string FormatReport(string server, string bucket, S3FeatureCheck[] results)
        {
            var lines = new List<string> { $"S3 feature check: {server}, bucket '{bucket}'" };
            lines.AddRange(results.Select(r => $"{(r.Supported ? "OK  " : "FAIL")}  {r.Feature}: {r.Details}"));
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>Round-trips a value with Swedish letters and an emoji through upload + HeadObject with the configured encoding.</summary>
        private static S3FeatureCheck CheckNonAsciiMetadata(AmazonS3Client client, ResolvedConnection connection, string bucket, string key)
        {
            string name = $"Non-ASCII metadata ({connection.MetadataEncoding})";
            if (connection.MetadataEncoding == MetadataEncoding.Reject)
                return new S3FeatureCheck { Feature = name, Supported = true, Details = "Skipped: Metadata Encoding is Reject." };

            const string expected = "Åsa Öberg, Göteborg ✓ 😀";
            S3FeatureCheck result = Check(name, "Needed for metadata values with å, ä, ö etc.", () =>
            {
                var put = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x", ContentType = "text/plain" };
                S3Helper.AddMetadata(put.Metadata, new[] { new S3KeyValue { Key = "decisions-check", Value = expected } }, connection.MetadataEncoding);
                S3ClientFactory.ApplyCompatibility(put, connection);
                client.PutObjectAsync(put).GetAwaiter().GetResult();

                GetObjectMetadataResponse head = client.GetObjectMetadataAsync(bucket, key).GetAwaiter().GetResult();
                string actual = S3Helper.ReadMetadata(head.Metadata, connection.MetadataEncoding).FirstOrDefault(m => m.Key == "decisions-check")?.Value;
                if (actual != expected)
                    throw new Exception($"Sent '{expected}', got back '{actual}'. Try another Metadata Encoding connection setting.");
            });
            try { client.DeleteObjectAsync(bucket, key).GetAwaiter().GetResult(); } catch { /* best effort cleanup */ }
            return result;
        }

        private static S3FeatureCheck Check(string feature, string purpose, Action action, string featureName = null)
        {
            return Check(feature, purpose, () => { action(); return null; }, featureName);
        }

        private static S3FeatureCheck Check(string feature, string purpose, Func<string> action, string featureName = null)
        {
            try
            {
                string info = action();
                return new S3FeatureCheck { Feature = feature, Supported = true, Details = info ?? purpose };
            }
            catch (Exception ex)
            {
                return new S3FeatureCheck { Feature = feature, Supported = false, Details = $"{S3Helper.DescribeException(ex, featureName)} ({purpose})" };
            }
        }
    }
}
