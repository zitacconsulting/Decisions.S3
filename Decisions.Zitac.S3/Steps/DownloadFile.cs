using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Data.DataTypes;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>GetObject into memory, returned as a Decisions FileData plus the file's info and metadata.</summary>
    [AutoRegisterStep("Download File", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class DownloadFile : BaseS3Step, IDefaultInputMappingStep
    {
        private const string INPUT_VERSION_ID = "Version Id";

        protected override bool SupportsNotFoundOption => true;

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
            new DataDescription(typeof(FileData), "File"),
            new DataDescription(typeof(S3FileInfo), "File Info")
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            string key = S3Helper.NormalizeKey(GetString(data, INPUT_KEY));
            string versionId = GetString(data, INPUT_VERSION_ID);

            var request = new GetObjectRequest { BucketName = bucket.Trim(), Key = key };
            if (!string.IsNullOrWhiteSpace(versionId))
                request.VersionId = versionId.Trim();

            using GetObjectResponse response = client.GetObjectAsync(request).GetAwaiter().GetResult();
            using var buffer = new MemoryStream();
            response.ResponseStream.CopyTo(buffer);

            string fileName = S3Helper.FileNameFromKey(key);
            var info = new S3FileInfo
            {
                Bucket = bucket.Trim(),
                Key = key,
                FileName = fileName,
                Size = buffer.Length,
                LastModified = response.LastModified,
                ETag = response.ETag,
                ContentType = response.Headers?.ContentType,
                VersionId = response.VersionId,
                StorageClass = response.StorageClass?.Value,
                Metadata = S3Helper.ReadMetadata(response.Metadata, connection.MetadataEncoding)
            };

            return new Dictionary<string, object>
            {
                { "File", new FileData(fileName, buffer.ToArray()) },
                { "File Info", info }
            };
        }
    }
}
