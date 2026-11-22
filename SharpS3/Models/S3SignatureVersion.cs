namespace SharpS3.Models;

using System.Runtime.Serialization;

/// <summary>
/// S3 signature version.
/// </summary>
public enum S3SignatureVersion
{
    /// <summary>
    /// Unknown.
    /// </summary>
    [EnumMember(Value = "Unknown")]
    Unknown,
    /// <summary>
    /// Version2.
    /// </summary>
    [EnumMember(Value = "Version2")]
    Version2,
    /// <summary>
    /// Version4.
    /// </summary>
    [EnumMember(Value = "Version4")]
    Version4
}
