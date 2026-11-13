using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;
using DecisionsFramework.Design.Properties;
using DecisionsFramework.Design.Properties.Attributes;

namespace Decisions.Zitac.S3.Steps
{
    public enum MetadataHandling
    {
        /// <summary>Copy the source's metadata unchanged (S3 MetadataDirective COPY).</summary>
        Keep,
        /// <summary>Source metadata plus the given entries (same key = new value).</summary>
        Merge,
        /// <summary>Only the given entries.</summary>
        Replace
    }

    /// <summary>
    /// CopyObject (server-side, the data doesn't pass through Decisions; max 5 GB per S3), optionally followed by
    /// deleting the source (= move; not atomic: if the delete fails, the copy exists and the step reports the error).
    ///
    /// Copying a file onto itself with Merge/Replace is the standard S3 way to change metadata after upload.
    /// With Merge/Replace S3 also drops Content-Type and the other stored headers unless they are sent again,
    /// so they are read from the source first and carried over. Tags are copied by the server's default behavior.
    /// </summary>
    [AutoRegisterStep("Copy or Move File", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class CopyFile : BaseS3Step, IDefaultInputMappingStep
    {
        private const string INPUT_SOURCE_BUCKET = "Source Bucket";
        private const string INPUT_SOURCE_KEY = "Source Key";
        private const string INPUT_SOURCE_VERSION_ID = "Source Version Id";
        private const string INPUT_DEST_BUCKET = "Destination Bucket";
        private const string INPUT_DEST_KEY = "Destination Key";
        private const string INPUT_METADATA = "Metadata";
        private const string INPUT_CONTENT_TYPE = "Content Type";

        [WritableValue] private bool deleteSource;

        protected override bool SupportsNotFoundOption => true;

        [WritableValue] private MetadataHandling metadataHandling = MetadataHandling.Keep;

        [PropertyClassification(new[] { INPUTS_CATEGORY }, "Copy Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string InputsNote
        {
            get { return "Destination Bucket ignored = same bucket. Destination Key ignored = same key (only useful with Merge/Replace: changes the metadata of the file in place). Delete Source makes it a move."; }
            set { }
        }

        [PropertyClassification(1, "Delete Source (Move)", INPUTS_CATEGORY)]
        public bool DeleteSource
        {
            get { return deleteSource; }
            set { deleteSource = value; OnPropertyChanged(nameof(DeleteSource)); }
        }

        [PropertyClassification(2, "Metadata Handling", INPUTS_CATEGORY)]
        public MetadataHandling MetadataHandling
        {
            get { return metadataHandling; }
            set
            {
                metadataHandling = value;
                OnPropertyChanged(nameof(MetadataHandling));
                OnPropertyChanged(nameof(InputData));
            }
        }

        protected override IEnumerable<DataDescription> StepInputs
        {
            get
            {
                var inputs = new List<DataDescription>
                {
                    new DataDescription(typeof(string), INPUT_SOURCE_BUCKET),
                    new DataDescription(typeof(string), INPUT_SOURCE_KEY),
                    new DataDescription(typeof(string), INPUT_SOURCE_VERSION_ID),
                    new DataDescription(typeof(string), INPUT_DEST_BUCKET),
                    new DataDescription(typeof(string), INPUT_DEST_KEY)
                };
                if (metadataHandling != MetadataHandling.Keep)
                {
                    inputs.Add(new DataDescription(typeof(S3KeyValue), INPUT_METADATA, true));
                    inputs.Add(new DataDescription(typeof(string), INPUT_CONTENT_TYPE));
                }
                return inputs;
            }
        }

        public IInputMapping[] DefaultInputs => new IInputMapping[]
        {
            new IgnoreInputMapping { InputDataName = INPUT_SOURCE_VERSION_ID },
            new IgnoreInputMapping { InputDataName = INPUT_DEST_BUCKET },
            new IgnoreInputMapping { InputDataName = INPUT_DEST_KEY },
            new IgnoreInputMapping { InputDataName = INPUT_CONTENT_TYPE }
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(string), "Destination Bucket"),
            new DataDescription(typeof(string), "Destination Key"),
            new DataDescription(typeof(string), "ETag"),
            new DataDescription(typeof(string), "Version Id")
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string sourceBucket = GetString(data, INPUT_SOURCE_BUCKET);
            S3Helper.RequireBucket(sourceBucket);
            sourceBucket = sourceBucket.Trim();
            string sourceKey = S3Helper.NormalizeKey(GetString(data, INPUT_SOURCE_KEY));
            string sourceVersionId = GetString(data, INPUT_SOURCE_VERSION_ID);
            if (string.IsNullOrWhiteSpace(sourceVersionId))
                sourceVersionId = null;

            string destBucket = GetString(data, INPUT_DEST_BUCKET);
            destBucket = string.IsNullOrWhiteSpace(destBucket) ? sourceBucket : destBucket.Trim();
            string destKey = GetString(data, INPUT_DEST_KEY);
            destKey = string.IsNullOrWhiteSpace(destKey) ? sourceKey : S3Helper.NormalizeKey(destKey);

            bool sameObject = destBucket == sourceBucket && destKey == sourceKey;
            if (sameObject && metadataHandling == MetadataHandling.Keep)
                throw new ArgumentException("Source and destination are the same file. Choose another destination, or set Metadata Handling to Merge or Replace to change the file's metadata.");

            var request = new CopyObjectRequest
            {
                SourceBucket = sourceBucket,
                SourceKey = sourceKey,
                DestinationBucket = destBucket,
                DestinationKey = destKey
            };
            if (sourceVersionId != null)
                request.SourceVersionId = sourceVersionId.Trim();

            if (metadataHandling != MetadataHandling.Keep)
                ApplyNewMetadata(request, data, client, connection, sourceVersionId);

            CopyObjectResponse response = client.CopyObjectAsync(request).GetAwaiter().GetResult();

            if (deleteSource && !sameObject)
            {
                var delete = new DeleteObjectRequest { BucketName = sourceBucket, Key = sourceKey };
                // Deleting a specific version removes it for good; without one, a versioned bucket only adds a delete marker.
                if (sourceVersionId != null)
                    delete.VersionId = sourceVersionId.Trim();
                try
                {
                    client.DeleteObjectAsync(delete).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    throw new Exception($"The file was copied to '{destKey}', but deleting the source failed: {S3Helper.DescribeException(ex)}", ex);
                }
            }

            return new Dictionary<string, object>
            {
                { "Destination Bucket", destBucket },
                { "Destination Key", destKey },
                { "ETag", response.ETag },
                { "Version Id", response.VersionId }
            };
        }

        private void ApplyNewMetadata(CopyObjectRequest request, StepStartData data, AmazonS3Client client, ResolvedConnection connection, string sourceVersionId)
        {
            var head = new GetObjectMetadataRequest { BucketName = request.SourceBucket, Key = request.SourceKey };
            if (sourceVersionId != null)
                head.VersionId = sourceVersionId.Trim();
            GetObjectMetadataResponse source = client.GetObjectMetadataAsync(head).GetAwaiter().GetResult();

            S3KeyValue[] given = (GetValue<S3KeyValue[]>(data, INPUT_METADATA) ?? Array.Empty<S3KeyValue>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Key))
                .ToArray();

            S3KeyValue[] metadata = given;
            if (metadataHandling == MetadataHandling.Merge)
            {
                // Decoded source values are re-encoded below with the current Metadata Encoding.
                var givenKeys = new HashSet<string>(given.Select(m => NormalizeMetadataKey(m.Key)), StringComparer.Ordinal);
                metadata = S3Helper.ReadMetadata(source.Metadata, connection.MetadataEncoding)
                    .Where(m => !givenKeys.Contains(NormalizeMetadataKey(m.Key)))
                    .Concat(given)
                    .ToArray();
            }

            request.MetadataDirective = S3MetadataDirective.REPLACE;
            S3Helper.AddMetadata(request.Metadata, metadata, connection.MetadataEncoding);

            // REPLACE also replaces these stored headers; send them again so they aren't lost.
            string contentType = GetString(data, INPUT_CONTENT_TYPE);
            request.ContentType = string.IsNullOrWhiteSpace(contentType) ? source.Headers?.ContentType : contentType.Trim();
            if (!string.IsNullOrEmpty(source.Headers?.CacheControl)) request.Headers.CacheControl = source.Headers.CacheControl;
            if (!string.IsNullOrEmpty(source.Headers?.ContentDisposition)) request.Headers.ContentDisposition = source.Headers.ContentDisposition;
            if (!string.IsNullOrEmpty(source.Headers?.ContentEncoding)) request.Headers.ContentEncoding = source.Headers.ContentEncoding;
        }

        private static string NormalizeMetadataKey(string key)
        {
            key = key.Trim();
            if (key.StartsWith(S3Helper.METADATA_HEADER_PREFIX, StringComparison.OrdinalIgnoreCase))
                key = key.Substring(S3Helper.METADATA_HEADER_PREFIX.Length);
            return key.ToLowerInvariant();
        }
    }
}
