namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// JSON type enum. 
    /// </summary>
    public enum JsonTypeEnum
    {
        /// <summary>
        /// DOCUMENT.
        /// </summary>
        [EnumMember(Value = "DOCUMENT")]
        [XmlEnum(Name = "DOCUMENT")]
        DOCUMENT,
        /// <summary>
        /// LINES.
        /// </summary>
        [EnumMember(Value = "LINES")]
        [XmlEnum(Name = "LINES")]
        LINES,
    }
}
