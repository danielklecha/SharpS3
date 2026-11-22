namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// Retention modes used by S3. 
    /// </summary>
    public enum RetentionModeEnum
    {
        /// <summary>
        /// Null.
        /// </summary>
        [EnumMember(Value = null)]
        [XmlEnum(Name = null)]
        None,
        /// <summary>
        /// Governance.
        /// </summary>
        [EnumMember(Value = "GOVERNANCE")]
        [XmlEnum(Name = "GOVERNANCE")]
        Governance,
        /// <summary>
        /// Compliance.
        /// </summary>
        [EnumMember(Value = "COMPLIANCE")]
        [XmlEnum(Name = "COMPLIANCE")]
        Compliance
    }
}
