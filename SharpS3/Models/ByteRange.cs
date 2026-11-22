namespace SharpS3.Models;

/// <summary>
/// Specifies a byte range for S3 GetObject requests.
/// </summary>
public readonly struct ByteRange
{
    /// <summary>
    /// Gets the inclusive starting byte position of the range, if specified.
    /// </summary>
    public long? Start { get; }

    /// <summary>
    /// Gets the inclusive ending byte position of the range, if specified.
    /// </summary>
    public long? End { get; }

    /// <summary>
    /// Gets the suffix length specifying the number of bytes from the end of the object, if specified.
    /// </summary>
    public long? SuffixLength { get; }

    public ByteRange(long start, long end)
    {
        Start = start;
        End = end;
        SuffixLength = null;
    }

    /// <summary>
    /// Creates a byte range starting from the specified byte offset to the end of the object.
    /// </summary>
    public static ByteRange FromStart(long start) => new ByteRange(start, null, null);

    /// <summary>
    /// Creates a byte range starting from <paramref name="start"/> through <paramref name="end"/> inclusive.
    /// </summary>
    public static ByteRange FromStartEnd(long start, long end) => new ByteRange(start, end, null);

    /// <summary>
    /// Creates a suffix byte range covering the last <paramref name="suffixLength"/> bytes of the object.
    /// </summary>
    public static ByteRange FromSuffix(long suffixLength) => new ByteRange(null, null, suffixLength);

    private ByteRange(long? start, long? end, long? suffixLength)
    {
        Start = start;
        End = end;
        SuffixLength = suffixLength;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (SuffixLength.HasValue) return $"bytes=-{SuffixLength.Value}";
        if (Start.HasValue && End.HasValue) return $"bytes={Start.Value}-{End.Value}";
        if (Start.HasValue) return $"bytes={Start.Value}-";
        return string.Empty;
    }
}
