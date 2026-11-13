using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Data.DataTypes;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;
using DecisionsFramework.Design.Properties;
using DecisionsFramework.Design.Properties.Attributes;
using Microsoft.AspNetCore.StaticFiles;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// Single PutObject with optional user metadata and tags. Metadata works on every S3 server; tags are
    /// sent as the x-amz-tagging header, which some servers don't support (the step then fails with a clear message).
    /// TODO: multipart upload for very large files, if needed (single PUT allows up to 5 GB).
    /// </summary>
    [AutoRegisterStep("Upload File", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    public class UploadFile : BaseS3Step, IDefaultInputMappingStep
    {
        private const string INPUT_FOLDER = "Folder";
        private const string INPUT_FILE = "File";
        private const string INPUT_FILE_NAME = "File Name";
        private const string INPUT_CONTENT_TYPE = "Content Type";
        private const string INPUT_METADATA = "Metadata";
        private const string INPUT_TAGS = "Tags";
        private const string DEFAULT_CONTENT_TYPE = "application/octet-stream";

        private static readonly FileExtensionContentTypeProvider contentTypeProvider = CreateContentTypeProvider();

        private static FileExtensionContentTypeProvider CreateContentTypeProvider()
        {
            var provider = new FileExtensionContentTypeProvider();
            provider.Mappings[".zip"] = "application/zip";
            return provider;
        }

        protected override string FeatureName => "uploading with these options (try without Tags)";

        [PropertyClassification(new[] { INPUTS_CATEGORY }, "Upload Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string InputsNote
        {
            get { return "Key = Folder + File Name (File Name defaults to the file's own name). Content Type is detected from the name if ignored. Metadata keys: a-z, 0-9, '-' and '_'; non-ASCII values are encoded per the Metadata Encoding setting. Tags: max 10, letters, numbers, spaces and + - = . _ : / @ only (some servers reject å/ä/ö), and some servers ignore tags silently; Check S3 Features shows whether tagging works."; }
            set { }
        }

        protected override IEnumerable<DataDescription> StepInputs => new[]
        {
            new DataDescription(typeof(string), INPUT_BUCKET),
            new DataDescription(typeof(string), INPUT_FOLDER),
            new DataDescription(typeof(FileData), INPUT_FILE),
            new DataDescription(typeof(string), INPUT_FILE_NAME),
            new DataDescription(typeof(string), INPUT_CONTENT_TYPE),
            new DataDescription(typeof(S3KeyValue), INPUT_METADATA, true),
            new DataDescription(typeof(S3KeyValue), INPUT_TAGS, true)
        };

        public IInputMapping[] DefaultInputs => new IInputMapping[]
        {
            new IgnoreInputMapping { InputDataName = INPUT_FOLDER },
            new IgnoreInputMapping { InputDataName = INPUT_FILE_NAME },
            new IgnoreInputMapping { InputDataName = INPUT_CONTENT_TYPE },
            new IgnoreInputMapping { InputDataName = INPUT_METADATA },
            new IgnoreInputMapping { InputDataName = INPUT_TAGS }
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(string), INPUT_KEY),
            new DataDescription(typeof(string), "Content Type"),
            new DataDescription(typeof(string), "ETag"),
            new DataDescription(typeof(string), "Version Id")
        };

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);

            FileData file = GetValue<FileData>(data, INPUT_FILE);
            byte[] contents = file?.Contents;
            if (contents == null)
                throw new ArgumentException("File is required");

            string fileName = GetString(data, INPUT_FILE_NAME);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = file.FileName;
            string key = S3Helper.CombineKey(GetString(data, INPUT_FOLDER), fileName);

            string contentType = GetString(data, INPUT_CONTENT_TYPE);
            if (string.IsNullOrWhiteSpace(contentType))
                contentType = contentTypeProvider.TryGetContentType(key, out string detected) ? detected : DEFAULT_CONTENT_TYPE;

            using var stream = new MemoryStream(contents);
            var request = new PutObjectRequest
            {
                BucketName = bucket.Trim(),
                Key = key,
                InputStream = stream,
                ContentType = contentType.Trim()
            };
            S3Helper.AddMetadata(request.Metadata, GetValue<S3KeyValue[]>(data, INPUT_METADATA), connection.MetadataEncoding);
            List<Tag> tags = S3Helper.ToTags(GetValue<S3KeyValue[]>(data, INPUT_TAGS));
            if (tags.Count > 0)
                request.TagSet = tags;
            S3ClientFactory.ApplyCompatibility(request, connection);

            PutObjectResponse response = client.PutObjectAsync(request).GetAwaiter().GetResult();

            return new Dictionary<string, object>
            {
                { INPUT_KEY, key },
                { "Content Type", request.ContentType },
                { "ETag", response.ETag },
                { "Version Id", response.VersionId }
            };
        }
    }
}
