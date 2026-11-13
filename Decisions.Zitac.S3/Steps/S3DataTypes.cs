using System.Runtime.Serialization;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>A metadata entry or tag.</summary>
    [DataContract]
    public class S3KeyValue
    {
        [DataMember]
        public string Key { get; set; }

        [DataMember]
        public string Value { get; set; }
    }

    [DataContract]
    public class S3FileInfo
    {
        [DataMember]
        public string Bucket { get; set; }

        [DataMember]
        public string Key { get; set; }

        [DataMember]
        public string FileName { get; set; }

        [DataMember]
        public long Size { get; set; }

        [DataMember]
        public DateTime? LastModified { get; set; }

        [DataMember]
        public string ETag { get; set; }

        /// <summary>Only filled by Get File Info / Download (listing doesn't return it).</summary>
        [DataMember]
        public string ContentType { get; set; }

        [DataMember]
        public string VersionId { get; set; }

        [DataMember]
        public string StorageClass { get; set; }

        /// <summary>User metadata (x-amz-meta-*), without the prefix. Only filled by Get File Info / Download, or by a search with metadata filter.</summary>
        [DataMember]
        public S3KeyValue[] Metadata { get; set; }

        /// <summary>Only filled when tags were requested.</summary>
        [DataMember]
        public S3KeyValue[] Tags { get; set; }
    }
}
