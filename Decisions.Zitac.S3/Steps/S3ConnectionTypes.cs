using System.Runtime.Serialization;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow.Service.Debugging.DebugData;
using DecisionsFramework.Design.Properties.Attributes;
using DecisionsFramework.ServiceLayer.Utilities;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// The "Credentials" input of every step: everything that typically differs between environments (test/prod).
    /// The technical connection settings are step properties (or the optional "Connection Settings" input).
    /// </summary>
    [AutoRegisterNativeType]
    [DataContract]
    public class S3Credentials : IDebuggerJsonProvider
    {
        /// <summary>e.g. https://s3.company.local:9000. Empty = Amazon S3 (then Region is required).</summary>
        [DataMember(Order = 0)]
        [WritableValue]
        public string ServerUrl { get; set; }

        /// <summary>Leave empty unless the S3 administrator gives one (empty = us-east-1). Required for Amazon S3.</summary>
        [DataMember(Order = 1)]
        [WritableValue]
        public string Region { get; set; }

        /// <summary>Empty (with an empty secret) = anonymous access to public buckets.</summary>
        [DataMember(Order = 2)]
        [WritableValue]
        public string AccessKeyId { get; set; }

        [DataMember(Order = 3)]
        [WritableValue]
        [PasswordText]
        public string SecretAccessKey { get; set; }

        public object GetJsonDebugView() => new
        {
            ServerUrl,
            Region,
            AccessKeyId,
            SecretAccessKey = string.IsNullOrEmpty(SecretAccessKey) ? "" : "********"
        };
    }

    /// <summary>
    /// The optional "Connection Settings" input (when a step has Provide Settings as Input ticked); otherwise the same
    /// values are step properties. Every field's empty value (null, false, 0, first enum value: what Build Data starts
    /// with) is the most compatible choice.
    /// </summary>
    [AutoRegisterNativeType]
    [DataContract]
    public class S3ConnectionSettings
    {
        /// <summary>https://bucket.server/key instead of https://server/bucket/key. Needs wildcard DNS (and certificate) on the server.</summary>
        [DataMember(Order = 0)]
        [WritableValue]
        public bool UseVirtualHostedStyle { get; set; }

        /// <summary>Send the CRC checksums and aws-chunked uploads AWS SDK v4 uses by default; many S3-compatible servers reject them.</summary>
        [DataMember(Order = 1)]
        [WritableValue]
        public bool UseAwsChecksums { get; set; }

        /// <summary>V1 only for old servers that fail on listing with V2.</summary>
        [DataMember(Order = 2)]
        [WritableValue]
        public ListApiVersion ListApiVersion { get; set; }

        /// <summary>Skip TLS certificate checks. Last resort; trusting the server's CA on the Decisions server is the right fix.</summary>
        [DataMember(Order = 3)]
        [WritableValue]
        public bool AcceptUntrustedCertificate { get; set; }

        /// <summary>Per request. 0 = 100 seconds.</summary>
        [DataMember(Order = 4)]
        [WritableValue]
        public int TimeoutSeconds { get; set; }

        /// <summary>How metadata values with non-ASCII characters (å, ä, ö, ...) are stored. Default RFC 2047.</summary>
        [DataMember(Order = 5)]
        [WritableValue]
        public MetadataEncoding MetadataEncoding { get; set; }
    }
}
