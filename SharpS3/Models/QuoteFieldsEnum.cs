namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// Quote fields options. 
    /// </summary>
    public enum QuoteFieldsEnum
    {
        /// <summary>
        /// ALWAYS.
        /// </summary>
        [EnumMember(Value = "ALWAYS")]
        [XmlEnum(Name = "ALWAYS")]
        ALWAYS,
        /// <summary>
        /// ASNEEDED.
        /// </summary>
        [EnumMember(Value = "ASNEEDED")]
        [XmlEnum(Name = "ASNEEDED")]
        ASNEEDED,
    }
}
