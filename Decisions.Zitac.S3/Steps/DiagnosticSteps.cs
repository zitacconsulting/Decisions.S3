using System.Runtime.Serialization;
using Amazon.S3;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;
using DecisionsFramework.Design.Properties;
using DecisionsFramework.Design.Properties.Attributes;

namespace Decisions.Zitac.S3.Steps
{
    [DataContract]
    public class S3FeatureCheck
    {
        [DataMember]
        public string Feature { get; set; }

        [DataMember]
        public bool Supported { get; set; }

        [DataMember]
        public string Details { get; set; }
    }

    /// <summary>
    /// Read-only check that the Connection and Credentials work. The Error outcome's message says what to fix.
    /// Also reports the server software, when the server says (useful when nobody knows what the S3 storage runs on).
    /// </summary>
    [AutoRegisterStep("Test S3 Connection", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class TestS3Connection : BaseS3Step, IDefaultInputMappingStep
    {
        [PropertyClassification(new[] { INPUTS_CATEGORY }, "Test Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string InputsNote
        {
            get { return "Checks that the Credentials work. With a Bucket (recommended), the step succeeds if that bucket can be read; Buckets is also filled if the credentials may list all buckets (keys limited to one bucket often may not, and Message then says so). Without a Bucket, the step lists the buckets."; }
            set { }
        }

        protected override IEnumerable<DataDescription> StepInputs => new[]
        {
            new DataDescription(typeof(string), INPUT_BUCKET)
        };

        public IInputMapping[] DefaultInputs => new IInputMapping[]
        {
            new IgnoreInputMapping { InputDataName = INPUT_BUCKET }
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(string), "Message"),
            new DataDescription(typeof(string), "Server Software"),
            new DataDescription(typeof(string), "Buckets", true)
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET)?.Trim();
            string serverSoftware = S3Diagnostics.GetServerSoftware(connection);
            string[] buckets = Array.Empty<string>();
            string message;

            if (string.IsNullOrEmpty(bucket))
            {
                buckets = S3Diagnostics.ListBuckets(client);
                message = $"Connected. {buckets.Length} bucket(s) visible.";
            }
            else
            {
                // The bucket check decides success; listing all buckets is a bonus, because access keys limited to
                // one bucket are often not allowed to list buckets.
                S3Diagnostics.ListFirstObject(client, connection, bucket);
                message = $"Connected. Bucket '{bucket}' can be read.";
                try
                {
                    buckets = S3Diagnostics.ListBuckets(client);
                    message += $" {buckets.Length} bucket(s) visible.";
                }
                catch (Exception ex)
                {
                    message += $" Listing all buckets isn't allowed for these credentials (that's normal for keys limited to one bucket): {S3Helper.DescribeException(ex)}";
                }
            }

            return new Dictionary<string, object>
            {
                { "Message", message },
                { "Server Software", serverSoftware },
                { "Buckets", buckets }
            };
        }
    }

    /// <summary>
    /// Finds out which features the S3 server supports by writing, reading and deleting small temporary files
    /// under decisions-s3-feature-check/ in the bucket. Run once when setting up a new S3 server.
    /// </summary>
    [AutoRegisterStep("Check S3 Features", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class CheckS3Features : BaseS3Step
    {
        [PropertyClassification(new[] { INPUTS_CATEGORY }, "Check Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string InputsNote
        {
            get { return "Writes, reads and deletes small temporary files under " + S3Diagnostics.FEATURE_CHECK_FOLDER + " in the Bucket, so the credentials need write access. Error = the bucket can't be reached at all (wrong bucket, credentials or server; the message says what to check). Done = the check ran: All Supported says whether everything works, Unsupported Features lists what doesn't, Report is a readable summary and Results has one row per feature."; }
            set { }
        }

        protected override IEnumerable<DataDescription> StepInputs => new[]
        {
            new DataDescription(typeof(string), INPUT_BUCKET)
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(bool), "All Supported"),
            new DataDescription(typeof(string), "Unsupported Features", true),
            new DataDescription(typeof(string), "Report"),
            new DataDescription(typeof(string), "Server Software"),
            new DataDescription(typeof(S3FeatureCheck), "Results", true)
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            bucket = bucket.Trim();

            string serverSoftware = S3Diagnostics.GetServerSoftware(connection);
            S3FeatureCheck[] results = S3Diagnostics.CheckFeatures(client, connection, bucket);
            string server = connection.IsAmazonS3 ? $"Amazon S3 ({connection.EffectiveSigningRegion})" : connection.EndpointUrl;

            string[] unsupported = results.Where(r => !r.Supported).Select(r => r.Feature).ToArray();

            return new Dictionary<string, object>
            {
                { "All Supported", unsupported.Length == 0 },
                { "Unsupported Features", unsupported },
                { "Report", S3Diagnostics.FormatReport($"{server} [{serverSoftware}]", bucket, results) },
                { "Server Software", serverSoftware },
                { "Results", results }
            };
        }
    }
}
