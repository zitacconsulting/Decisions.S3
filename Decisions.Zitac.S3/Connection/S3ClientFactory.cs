using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Decisions.Zitac.S3.Connection
{
    /// <summary>Builds AmazonS3Client instances configured for S3-compatible servers.</summary>
    public static class S3ClientFactory
    {
        public static AmazonS3Client Create(ResolvedConnection connection)
        {
            connection.Validate();

            var config = new AmazonS3Config
            {
                ForcePathStyle = connection.ForcePathStyle,
                Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
                MaxErrorRetry = 2
            };

            if (connection.IsAmazonS3)
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(connection.EffectiveSigningRegion);
            }
            else
            {
                config.ServiceURL = connection.EndpointUrl.Trim();
                // With a custom ServiceURL the SDK can't derive the signing region from the host name and
                // falls back to us-east-1, so always set it explicitly (see DecisionsKB notes/module-development.md).
                config.AuthenticationRegion = connection.EffectiveSigningRegion;
            }

            if (connection.CompatibilityMode)
            {
                // AWS SDK v4 adds CRC32 checksums (sent as aws-chunked trailers) to every upload by default;
                // many S3-compatible servers fail those requests. Only send checksums where S3 requires them.
                config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
                config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
            }

            if (connection.AcceptUntrustedCertificate)
                config.HttpClientFactory = new UntrustedCertificateHttpClientFactory();

            AWSCredentials credentials = string.IsNullOrWhiteSpace(connection.AccessKeyId)
                ? new AnonymousAWSCredentials()
                : new BasicAWSCredentials(connection.AccessKeyId.Trim(), connection.SecretAccessKey);

            return new AmazonS3Client(credentials, config);
        }

        /// <summary>Applies the per-request part of compatibility mode to an upload.</summary>
        public static void ApplyCompatibility(PutObjectRequest request, ResolvedConnection connection)
        {
            if (!connection.CompatibilityMode)
                return;

            // Sign the whole payload in one go instead of aws-chunked streaming signatures.
            request.UseChunkEncoding = false;
        }

        private sealed class UntrustedCertificateHttpClientFactory : HttpClientFactory
        {
            public override HttpClient CreateHttpClient(IClientConfig clientConfig)
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(handler) { Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(ResolvedConnection.DEFAULT_TIMEOUT_SECONDS) };
            }
        }
    }
}
