namespace SharpS3.Models;

using System.Runtime.Serialization;

/// <summary>
/// The type of request, specifically whether or not the bucket is included in the hostname.
/// </summary>
public enum S3RequestStyle
{
    /// <summary>
    /// Unknown request style.
    /// </summary>
    [EnumMember(Value = "Unknown")]
    Unknown,
    /// <summary>
    /// Path-style request, i.e. bucket is in the URL and not in the hostname, e.g. http://[hostname]/[bucket]/[key].
    /// </summary>
    [EnumMember(Value = "PathStyle")]
    PathStyle,
    /// <summary>
    /// Virtual-hosted style request, i.e. bucket is in the hostname and not in the URL, e.g. http://[bucket].[hostname]/[key].
    /// </summary>
    [EnumMember(Value = "VirtualHostedStyle")]
    VirtualHostedStyle
}
