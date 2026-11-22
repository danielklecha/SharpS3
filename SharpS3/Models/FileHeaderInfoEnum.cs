namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// File header info. 
    /// </summary>
    public enum FileHeaderInfoEnum
    {
        /// <summary>
        /// USE.
        /// </summary>
        [EnumMember(Value = "USE")]
        [XmlEnum(Name = "USE")]
        USE,
        /// <summary>
        /// IGNORE.
        /// </summary>
        [EnumMember(Value = "IGNORE")]
        [XmlEnum(Name = "IGNORE")]
        IGNORE,
        /// <summary>
        /// NONE.
        /// </summary>
        [EnumMember(Value = "NONE")]
        [XmlEnum(Name = "NONE")]
        NONE
    }
}
