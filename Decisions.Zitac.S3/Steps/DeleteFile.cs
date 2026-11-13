using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// DeleteObject. S3 returns success for a missing key too, so with Show Outcome For Not Found on, the step first
    /// checks that the file exists (one extra HEAD request) and goes to Not Found if it doesn't.
    /// </summary>
    [AutoRegisterStep("Delete File", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class DeleteFile : BaseS3Step, IDefaultInputMappingStep
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

        protected override DataDescription[] DoneOutputs => Array.Empty<DataDescription>();

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            var request = new DeleteObjectRequest { BucketName = bucket.Trim(), Key = S3Helper.NormalizeKey(GetString(data, INPUT_KEY)) };
            string versionId = GetString(data, INPUT_VERSION_ID);
            if (!string.IsNullOrWhiteSpace(versionId))
                request.VersionId = versionId.Trim();

            if (NotFoundOutcome != null)
            {
                // Throws NotFound (404) for a missing file, which the base step turns into the Not Found outcome.
                var head = new GetObjectMetadataRequest { BucketName = request.BucketName, Key = request.Key };
                if (request.VersionId != null)
                    head.VersionId = request.VersionId;
                client.GetObjectMetadataAsync(head).GetAwaiter().GetResult();
            }
            client.DeleteObjectAsync(request).GetAwaiter().GetResult();
            return new Dictionary<string, object>();
        }
    }
}
