namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// Permission types used by S3. 
    /// See https://docs.aws.amazon.com/AmazonS3/latest/dev/acl-overview.html for details.
    /// </summary>
    public enum MfaDeleteStatusEnum
    {
        /// <summary>
        /// Enabled.
        /// </summary>
        [EnumMember(Value = "Enabled")]
        [XmlEnum(Name = "Enabled")]
        Enabled,
        /// <summary>
        /// Disabled.
        /// </summary>
        [EnumMember(Value = "Disabled")]
        [XmlEnum(Name = "Disabled")]
        Disabled
    }
}
