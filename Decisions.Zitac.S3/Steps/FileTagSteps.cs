using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Properties;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// Object tags can be changed after upload (user metadata can't, short of copying the file onto itself).
    /// Not every S3-compatible server supports tagging; these steps then fail with "does not support object tagging".
    /// </summary>
    [AutoRegisterStep("Get File Tags", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class GetFileTags : BaseS3Step
    {
        protected override string FeatureName => "object tagging";

        protected override bool SupportsNotFoundOption => true;

        protected override IEnumerable<DataDescription> StepInputs => new[]
        {
            new DataDescription(typeof(string), INPUT_BUCKET),
            new DataDescription(typeof(string), INPUT_KEY)
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(S3KeyValue), "Tags", true)
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            GetObjectTaggingResponse response = client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = bucket.Trim(),
                Key = S3Helper.NormalizeKey(GetString(data, INPUT_KEY))
            }).GetAwaiter().GetResult();
            return new Dictionary<string, object> { { "Tags", S3Helper.FromTags(response.Tagging) } };
        }
    }

    /// <summary>Replaces all tags of a file (S3 has no "add one tag"; with Merge, existing tags are read first and kept).</summary>
    [AutoRegisterStep("Set File Tags", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class SetFileTags : BaseS3Step
    {
        [WritableValue]
        private bool mergeWithExisting;

        protected override bool SupportsNotFoundOption => true;

        protected override string FeatureName => "object tagging";

        [PropertyClassification(0, "Merge With Existing Tags", INPUTS_CATEGORY)]
        public bool MergeWithExisting
        {
            get { return mergeWithExisting; }
            set { mergeWithExisting = value; OnPropertyChanged(nameof(MergeWithExisting)); }
        }

        protected override IEnumerable<DataDescription> StepInputs => new[]
        {
            new DataDescription(typeof(string), INPUT_BUCKET),
            new DataDescription(typeof(string), INPUT_KEY),
            new DataDescription(typeof(S3KeyValue), "Tags", true)
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(S3KeyValue), "Tags", true)
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            bucket = bucket.Trim();
            string key = S3Helper.NormalizeKey(GetString(data, INPUT_KEY));
            S3KeyValue[] tags = GetValue<S3KeyValue[]>(data, "Tags") ?? Array.Empty<S3KeyValue>();

            if (mergeWithExisting)
            {
                S3KeyValue[] existing = S3Helper.FromTags(client.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key }).GetAwaiter().GetResult().Tagging);
                var newKeys = new HashSet<string>(tags.Where(t => t?.Key != null).Select(t => t.Key.Trim()), StringComparer.Ordinal);
                tags = existing.Where(e => !newKeys.Contains(e.Key)).Concat(tags).ToArray();
            }

            List<Tag> tagSet = S3Helper.ToTags(tags);
            if (tagSet.Count == 0)
                client.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key }).GetAwaiter().GetResult();
            else
                client.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = tagSet } }).GetAwaiter().GetResult();

            return new Dictionary<string, object> { { "Tags", S3Helper.FromTags(tagSet) } };
        }
    }
}
