namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// Compression type enum. 
    /// </summary>
    public enum CompressionTypeEnum
    {
        /// <summary>
        /// NONE.
        /// </summary>
        [EnumMember(Value = "NONE")]
        [XmlEnum(Name = "NONE")]
        NONE,
        /// <summary>
        /// GZIP.
        /// </summary>
        [EnumMember(Value = "GZIP")]
        [XmlEnum(Name = "GZIP")]
        GZIP,
        /// <summary>
        /// BZIP2.
        /// </summary>
        [EnumMember(Value = "BZIP2")]
        [XmlEnum(Name = "BZIP2")]
        BZIP2,
    }
}
