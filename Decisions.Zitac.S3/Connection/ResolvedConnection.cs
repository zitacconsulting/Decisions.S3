using Decisions.Zitac.S3.Steps;

namespace Decisions.Zitac.S3.Connection
{
    public enum ListApiVersion
    {
        /// <summary>ListObjectsV2 (default; every current S3 server supports it).</summary>
        V2,
        /// <summary>The original ListObjects call, for old servers that answer V2 with an error.</summary>
        V1
    }

    /// <summary>
    /// The Credentials input + connection settings (step properties or input) with defaults applied and the user-facing "opt-in" flags turned
    /// into what the client needs. Everything below works on this, never on the raw inputs.
    /// </summary>
    public class ResolvedConnection
    {
        public const string DEFAULT_SIGNING_REGION = "us-east-1";
        public const int DEFAULT_TIMEOUT_SECONDS = 100;

        public string EndpointUrl { get; set; }
        public string SigningRegion { get; set; }
        public string AccessKeyId { get; set; }
        public string SecretAccessKey { get; set; }
        public bool ForcePathStyle { get; set; } = true;
        /// <summary>No default checksums / aws-chunked uploads (see S3ClientFactory).</summary>
        public bool CompatibilityMode { get; set; } = true;
        public ListApiVersion ListApiVersion { get; set; } = ListApiVersion.V2;
        public bool AcceptUntrustedCertificate { get; set; }
        public int TimeoutSeconds { get; set; } = DEFAULT_TIMEOUT_SECONDS;
        public MetadataEncoding MetadataEncoding { get; set; } = MetadataEncoding.Rfc2047;

        public bool IsAnonymous => string.IsNullOrWhiteSpace(AccessKeyId);

        public bool IsAmazonS3 => string.IsNullOrWhiteSpace(EndpointUrl);

        public string EffectiveSigningRegion => string.IsNullOrWhiteSpace(SigningRegion) ? DEFAULT_SIGNING_REGION : SigningRegion.Trim();

        /// <param name="credentials">The Credentials input (server + keys).</param>
        /// <param name="settings">Step properties or the Connection Settings input; null = all defaults.</param>
        public static ResolvedConnection From(S3Credentials credentials, S3ConnectionSettings settings)
        {
            if (credentials == null)
                throw new ArgumentException("Credentials are required: set at least Server URL (or Region, for Amazon S3)");

            settings ??= new S3ConnectionSettings();
            return new ResolvedConnection
            {
                EndpointUrl = credentials.ServerUrl?.Trim(),
                SigningRegion = credentials.Region?.Trim(),
                AccessKeyId = credentials.AccessKeyId?.Trim(),
                // Trimmed like the key: a space copied along with the secret would only give a signature error.
                SecretAccessKey = credentials.SecretAccessKey?.Trim(),
                ForcePathStyle = !settings.UseVirtualHostedStyle,
                CompatibilityMode = !settings.UseAwsChecksums,
                ListApiVersion = settings.ListApiVersion,
                AcceptUntrustedCertificate = settings.AcceptUntrustedCertificate,
                TimeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : DEFAULT_TIMEOUT_SECONDS,
                MetadataEncoding = settings.MetadataEncoding
            };
        }

        public void Validate()
        {
            if (IsAmazonS3)
            {
                if (string.IsNullOrWhiteSpace(SigningRegion))
                    throw new ArgumentException("Credentials: set Server URL (e.g. https://s3.company.local:9000), or Region (e.g. eu-north-1) for Amazon S3");
            }
            else if (!Uri.TryCreate(EndpointUrl, UriKind.Absolute, out Uri uri)
                     || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException($"Credentials: Server URL '{EndpointUrl}' must be a full address starting with https:// or http://, e.g. https://s3.company.local:9000");
            }

            bool hasKey = !string.IsNullOrWhiteSpace(AccessKeyId);
            bool hasSecret = !string.IsNullOrWhiteSpace(SecretAccessKey);
            if (hasKey != hasSecret)
                throw new ArgumentException("Credentials: set both Access Key ID and Secret Access Key (or neither, for anonymous access)");
        }
    }
}
