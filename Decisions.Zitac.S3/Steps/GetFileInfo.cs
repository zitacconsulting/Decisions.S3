using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;
using DecisionsFramework.Design.Properties;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// HeadObject (works everywhere) plus, optionally, GetObjectTagging (not supported by every server).
    /// A missing file goes to the "Not Found" outcome instead of Error, so flows can branch on it.
    /// </summary>
    [AutoRegisterStep("Get File Info", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class GetFileInfo : BaseS3Step, IDefaultInputMappingStep
    {
        private const string INPUT_VERSION_ID = "Version Id";

        [WritableValue]
        private bool includeTags;

        protected override string FeatureName => includeTags ? "object tagging (untick Include Tags)" : null;

        [PropertyClassification(0, "Include Tags", INPUTS_CATEGORY)]
        public bool IncludeTags
        {
            get { return includeTags; }
            set { includeTags = value; OnPropertyChanged(nameof(IncludeTags)); }
        }

        protected override IEnumerable<DataDescription> StepInputs => new[]
        {
            new DataDescription(typeof(string), INPUT_BUCKET),
            new DataDescription(typeof(string), INPUT_KEY),
            new DataDescription(typeof(string), INPUT_VERSION_ID)
        };

        public IInputMapping[] DefaultInputs => new IInputMapping[]
        {
            new IgnoreInputMapping { InputDataName = INPUT_VERSION_ID }
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(S3FileInfo), "File Info")
        };

        // Always has the Not Found outcome (no checkbox): checking whether a file exists is what this step is for.
        protected override string NotFoundOutcome => OUTCOME_NOT_FOUND;

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            string key = S3Helper.NormalizeKey(GetString(data, INPUT_KEY));
            string versionId = GetString(data, INPUT_VERSION_ID);

            S3FileInfo info = ReadInfo(client, connection, bucket.Trim(), key, versionId, includeTags);
            return new Dictionary<string, object> { { "File Info", info } };
        }

        public static S3FileInfo ReadInfo(AmazonS3Client client, ResolvedConnection connection, string bucket, string key, string versionId, bool withTags)
        {
            var request = new GetObjectMetadataRequest { BucketName = bucket, Key = key };
            if (!string.IsNullOrWhiteSpace(versionId))
                request.VersionId = versionId.Trim();

            GetObjectMetadataResponse head;
            try
            {
                head = client.GetObjectMetadataAsync(request).GetAwaiter().GetResult();
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new FileNotFoundInS3Exception($"File '{key}' not found in bucket '{bucket}'");
            }

            var info = new S3FileInfo
            {
                Bucket = bucket,
                Key = key,
                FileName = S3Helper.FileNameFromKey(key),
                Size = head.ContentLength,
                LastModified = head.LastModified,
                ETag = head.ETag,
                ContentType = head.Headers?.ContentType,
                VersionId = head.VersionId,
                StorageClass = head.StorageClass?.Value,
                Metadata = S3Helper.ReadMetadata(head.Metadata, connection.MetadataEncoding)
            };

            if (withTags)
            {
                var tagRequest = new GetObjectTaggingRequest { BucketName = bucket, Key = key };
                if (!string.IsNullOrWhiteSpace(versionId))
                    tagRequest.VersionId = versionId.Trim();
                info.Tags = S3Helper.FromTags(client.GetObjectTaggingAsync(tagRequest).GetAwaiter().GetResult().Tagging);
            }

            return info;
        }
    }

    public class FileNotFoundInS3Exception : Exception
    {
        public FileNotFoundInS3Exception(string message) : base(message) { }
    }
}
