namespace SharpS3.Models;

using System.Xml.Serialization;

/// <summary>
/// Configuration for creating a bucket, optionally specifying a location constraint.
/// </summary>
[XmlRoot(ElementName = "CreateBucketConfiguration", Namespace = "http://s3.amazonaws.com/doc/2006-03-01/")]
public class CreateBucketConfiguration
{
    /// <summary>
    /// Location constraint (region).
    /// </summary>
    [XmlElement(ElementName = "LocationConstraint")]
    public string? LocationConstraint { get; set; }

    /// <summary>
    /// Default constructor.
    /// </summary>
    public CreateBucketConfiguration()
    {
    }

    /// <summary>
    /// Constructor with region.
    /// </summary>
    public CreateBucketConfiguration(string location)
    {
        LocationConstraint = location;
    }
}
