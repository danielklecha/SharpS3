namespace SharpS3.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Metadata for an S3 object returned by HeadObject.
/// </summary>
public class ObjectMetadataResponse
{
    /// <summary>
    /// Gets or sets the size of the object content in bytes.
    /// </summary>
    public long ContentLength { get; set; }

    /// <summary>
    /// Gets or sets the standard MIME type describing the format of the object.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the entity tag (ETag) representing the object.
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Gets or sets the date and time when the object was last modified.
    /// </summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>
    /// Gets or sets user-defined metadata associated with the object.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
