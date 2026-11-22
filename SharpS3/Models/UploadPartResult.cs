namespace SharpS3.Models;

/// <summary>
/// Result of uploading a part in a multipart upload.
/// </summary>
public class UploadPartResult
{
    /// <summary>
    /// Gets or sets the part number identifying the uploaded part within the multipart upload.
    /// </summary>
    public int PartNumber { get; set; }

    /// <summary>
    /// Gets or sets the entity tag (ETag) of the uploaded part.
    /// </summary>
    public string ETag { get; set; } = string.Empty;
}
