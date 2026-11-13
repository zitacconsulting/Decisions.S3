using System.Text;
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
    public enum PresignedUrlType
    {
        Download,
        Upload
    }

    /// <summary>
    /// A time-limited URL that lets anyone holding it download (GET) or upload (PUT) one file without credentials.
    /// Calculated locally with SigV4 query signing: no request is sent, so it works on any S3 server that
    /// supports SigV4, but it also isn't checked that the file exists.
    /// The signature covers the host name: the URL only works if users reach S3 under the same Server URL
    /// that Decisions uses.
    /// </summary>
    [AutoRegisterStep("Get Presigned URL", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class GetPresignedUrl : BaseS3Step, IDefaultInputMappingStep
    {
        private const string INPUT_EXPIRES_MINUTES = "Expires In Minutes";
        private const string INPUT_DOWNLOAD_FILE_NAME = "Download File Name";
        private const string INPUT_CONTENT_TYPE = "Content Type";
        private const int DEFAULT_EXPIRES_MINUTES = 60;
        // SigV4 presigned URLs are valid for at most 7 days.
        private const int MAX_EXPIRES_MINUTES = 7 * 24 * 60;

        [WritableValue] private PresignedUrlType urlType = PresignedUrlType.Download;

        [PropertyClassification(new[] { INPUTS_CATEGORY }, "Presigned URL Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string InputsNote
        {
            get
            {
                return urlType == PresignedUrlType.Download
                    ? "Anyone with the link can download the file until it expires (max 7 days). Download File Name makes browsers save it under that name. The link only works if users can reach the Server URL."
                    : "Anyone with the link can upload (HTTP PUT) to this key until it expires (max 7 days). If Content Type is set, the uploader must send exactly that Content-Type header.";
            }
            set { }
        }

        [PropertyClassification(1, "URL Type", INPUTS_CATEGORY)]
        public PresignedUrlType UrlType
        {
            get { return urlType; }
            set
            {
                urlType = value;
                OnPropertyChanged(nameof(UrlType));
                OnPropertyChanged(nameof(InputsNote));
                OnPropertyChanged(nameof(InputData));
            }
        }

        protected override IEnumerable<DataDescription> StepInputs
        {
            get
            {
                var inputs = new List<DataDescription>
                {
                    new DataDescription(typeof(string), INPUT_BUCKET),
                    new DataDescription(typeof(string), INPUT_KEY),
                    new DataDescription(typeof(int), INPUT_EXPIRES_MINUTES)
                };
                inputs.Add(urlType == PresignedUrlType.Download
                    ? new DataDescription(typeof(string), INPUT_DOWNLOAD_FILE_NAME)
                    : new DataDescription(typeof(string), INPUT_CONTENT_TYPE));
                return inputs;
            }
        }

        public IInputMapping[] DefaultInputs => new IInputMapping[]
        {
            new ConstantInputMapping { InputDataName = INPUT_EXPIRES_MINUTES, Value = DEFAULT_EXPIRES_MINUTES },
            new IgnoreInputMapping { InputDataName = INPUT_DOWNLOAD_FILE_NAME },
            new IgnoreInputMapping { InputDataName = INPUT_CONTENT_TYPE }
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(string), "URL"),
            new DataDescription(typeof(DateTime), "Expires")
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            if (string.IsNullOrWhiteSpace(connection.AccessKeyId))
                throw new ArgumentException("Presigned URLs need an access key; the connection is anonymous");

            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            string key = S3Helper.NormalizeKey(GetString(data, INPUT_KEY));

            int minutes = GetValue(data, INPUT_EXPIRES_MINUTES, DEFAULT_EXPIRES_MINUTES);
            if (minutes < 1 || minutes > MAX_EXPIRES_MINUTES)
                throw new ArgumentException($"Expires In Minutes must be between 1 and {MAX_EXPIRES_MINUTES} (7 days)");
            DateTime expires = DateTime.UtcNow.AddMinutes(minutes);

            var request = new GetPreSignedUrlRequest
            {
                BucketName = bucket.Trim(),
                Key = key,
                Expires = expires,
                Verb = urlType == PresignedUrlType.Upload ? HttpVerb.PUT : HttpVerb.GET
            };
            // Otherwise the SDK may build an https URL for an http-only endpoint.
            if (!connection.IsAmazonS3)
                request.Protocol = connection.EndpointUrl.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

            if (urlType == PresignedUrlType.Download)
            {
                string fileName = GetString(data, INPUT_DOWNLOAD_FILE_NAME);
                if (!string.IsNullOrWhiteSpace(fileName))
                    request.ResponseHeaderOverrides.ContentDisposition = ContentDispositionAttachment(fileName.Trim());
            }
            else
            {
                string contentType = GetString(data, INPUT_CONTENT_TYPE);
                if (!string.IsNullOrWhiteSpace(contentType))
                    request.ContentType = contentType.Trim();
            }

            string url = client.GetPreSignedURL(request);
            return new Dictionary<string, object>
            {
                { "URL", url },
                { "Expires", expires }
            };
        }

        /// <summary>attachment; filename="ascii fallback"; filename*=UTF-8''percent-encoded (RFC 6266), so å/ä/ö survive.</summary>
        private static string ContentDispositionAttachment(string fileName)
        {
            var fallback = new StringBuilder();
            foreach (char c in fileName)
                fallback.Append(c >= 0x20 && c <= 0x7E && c != '"' && c != '\\' ? c : '_');
            return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        }
    }
}
