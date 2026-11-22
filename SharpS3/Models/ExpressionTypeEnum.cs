namespace SharpS3.Models
{
    using System.Runtime.Serialization;
    using System.Xml.Serialization;

    /// <summary>
    /// Expression type enum. 
    /// </summary>
    public enum ExpressionTypeEnum
    {
        /// <summary>
        /// SQL.
        /// </summary>
        [EnumMember(Value = "SQL")]
        [XmlEnum(Name = "SQL")]
        SQL,
    }
}
