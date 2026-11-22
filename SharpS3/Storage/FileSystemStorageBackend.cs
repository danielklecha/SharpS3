namespace SharpS3.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Streams;

/// <summary>
/// Persistent disk-based S3 storage backend storing buckets as directories
/// and objects as files with streaming I/O.
/// </summary>
public class FileSystemStorageBackend : IS3StorageBackend
{
    /// <summary>
    /// Represents metadata persisted alongside a stored file object.
    /// </summary>
    [XmlRoot(ElementName = "FileMetadata")]
    public class FileMetadata
    {
        /// <summary>
        /// Gets or sets the MIME type of the stored file.
        /// </summary>
        [XmlElement(ElementName = "ContentType")]
        public string ContentType { get; set; } = "binary/octet-stream";

        /// <summary>
        /// Gets or sets the entity tag (ETag) of the stored file.
        /// </summary>
        [XmlElement(ElementName = "ETag")]
        public string ETag { get; set; } = "\"\"";

        /// <summary>
        /// Gets or sets the timestamp when the file was last modified.
        /// </summary>
        [XmlElement(ElementName = "LastModified")]
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the user metadata dictionary associated with the object.
        /// </summary>
        [XmlIgnore]
        public Dictionary<string, string> UserMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the array of metadata entries used for XML serialization.
        /// </summary>
        [XmlArray(ElementName = "UserMetadata")]
        [XmlArrayItem(ElementName = "Entry")]
        public MetadataEntry[] UserMetadataEntries
        {
            get => UserMetadata?.Select(kv => new MetadataEntry { Key = kv.Key, Value = kv.Value }).ToArray() ?? Array.Empty<MetadataEntry>();
            set
            {
                UserMetadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                UserMetadata.Clear();
                if (value != null)
                {
                    foreach (var e in value)
                    {
                        if (e.Key != null) UserMetadata[e.Key] = e.Value ?? string.Empty;
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets the object tagging set.
        /// </summary>
        [XmlElement(ElementName = "Tagging")]
        public Tagging? Tags { get; set; }

        /// <summary>
        /// Gets or sets the object legal hold status.
        /// </summary>
        [XmlElement(ElementName = "LegalHold")]
        public LegalHold? LegalHold { get; set; }

        /// <summary>
        /// Gets or sets the object retention configuration.
        /// </summary>
        [XmlElement(ElementName = "Retention")]
        public Retention? Retention { get; set; }

        /// <summary>
        /// Gets or sets the access control policy for the file.
        /// </summary>
        [XmlElement(ElementName = "AccessControlPolicy")]
        public AccessControlPolicy? Acl { get; set; }
    }

    /// <summary>
    /// Represents configuration and metadata persisted for an S3 bucket on disk.
    /// </summary>
    [XmlRoot(ElementName = "BucketMetadata")]
    public class BucketMetadata
    {
        /// <summary>
        /// Gets or sets the access control policy for the bucket.
        /// </summary>
        [XmlElement(ElementName = "AccessControlPolicy")]
        public AccessControlPolicy? Acl { get; set; }

        /// <summary>
        /// Gets or sets the bucket tagging configuration.
        /// </summary>
        [XmlElement(ElementName = "Tagging")]
        public Tagging? Tags { get; set; }

        /// <summary>
        /// Gets or sets the bucket versioning configuration.
        /// </summary>
        [XmlElement(ElementName = "VersioningConfiguration")]
        public VersioningConfiguration? Versioning { get; set; }

        /// <summary>
        /// Gets or sets the bucket website configuration.
        /// </summary>
        [XmlElement(ElementName = "WebsiteConfiguration")]
        public WebsiteConfiguration? Website { get; set; }

        /// <summary>
        /// Gets or sets the bucket logging status.
        /// </summary>
        [XmlElement(ElementName = "BucketLoggingStatus")]
        public BucketLoggingStatus? Logging { get; set; }
    }

    /// <summary>
    /// Represents a key-value pair of user metadata for XML serialization.
    /// </summary>
    public class MetadataEntry
    {
        /// <summary>
        /// Gets or sets the metadata key name.
        /// </summary>
        [XmlAttribute(AttributeName = "Key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metadata value.
        /// </summary>
        [XmlText]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents the internal state of an in-progress multipart upload on disk.
    /// </summary>
    private class UploadState
    {
        /// <summary>
        /// Gets or sets the unique multipart upload identifier.
        /// </summary>
        public string UploadId { get; set; } = "";

        /// <summary>
        /// Gets or sets the bucket name for the upload.
        /// </summary>
        public string Bucket { get; set; } = "";

        /// <summary>
        /// Gets or sets the object key for the upload.
        /// </summary>
        public string Key { get; set; } = "";

        /// <summary>
        /// Gets or sets the timestamp when the upload was initiated.
        /// </summary>
        public DateTime Initiated { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents an active write operation in progress.
    /// </summary>
    internal sealed class ActiveWriteState
    {
        public string FilePath { get; set; } = string.Empty;
        public DateTime Started { get; set; } = DateTime.UtcNow;
    }

    private readonly string _RootDirectory;
    private readonly string _UploadsDirectory;
    private readonly ConcurrentDictionary<string, UploadState> _ActiveUploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveWriteState> _ActiveWrites = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the dictionary of active writes currently in progress.
    /// </summary>
    internal ConcurrentDictionary<string, ActiveWriteState> ActiveWrites => _ActiveWrites;

    /// <summary>
    /// Default S3 owner for listings.
    /// </summary>
    public Owner DefaultOwner { get; set; } = new("filesystem-owner-id", "filesystem");

    /// <summary>
    /// When true, verifies that each part's ETag specified in CompleteMultipartUpload matches the uploaded part's ETag.
    /// Default is false for compatibility with test mocks, but recommended true in production.
    /// </summary>
    public bool ValidatePartETags { get; set; } = false;

    /// <summary>
    /// Optional action invoked when deleting an empty directory during parent directory pruning.
    /// Defaults to null, in which case <see cref="Directory.Delete(string)"/> is used.
    /// </summary>
    internal Action<string>? DirectoryDeleter { get; set; }

    /// <summary>
    /// Optional action invoked when deleting recursive staging directories.
    /// Defaults to null, in which case <see cref="Directory.Delete(string, bool)"/> is used.
    /// </summary>
    internal Action<string, bool>? RecursiveDirectoryDeleter { get; set; }

    /// <summary>
    /// Optional action invoked when deleting a file during cleanup or deletion.
    /// Defaults to null, in which case <see cref="File.Delete(string)"/> is used.
    /// </summary>
    internal Action<string>? FileDeleter { get; set; }

    private void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (FileDeleter != null)
            {
                FileDeleter(filePath);
            }
            else if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private void DeleteEmptyDirectory(string dirPath)
    {
        if (DirectoryDeleter != null)
        {
            DirectoryDeleter(dirPath);
        }
        else
        {
            Directory.Delete(dirPath);
        }
    }

    private void DeleteStagingDirectory(string stagingDir)
    {
        try
        {
            if (RecursiveDirectoryDeleter != null)
            {
                RecursiveDirectoryDeleter(stagingDir, true);
            }
            else
            {
                Directory.Delete(stagingDir, true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Initializes a new instance with the specified root directory.
    /// </summary>
    public FileSystemStorageBackend(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentNullException(nameof(rootDirectory));

        _RootDirectory = Path.GetFullPath(rootDirectory);
        _UploadsDirectory = Path.Combine(_RootDirectory, ".uploads");

        Directory.CreateDirectory(_RootDirectory);
        Directory.CreateDirectory(_UploadsDirectory);
    }

    private static readonly HashSet<string> _WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Checks whether the specified file or bucket name matches a Windows-reserved device name.
    /// </summary>
    internal static bool IsWindowsReservedName(string name)
    {
        string baseName = name;
        int dotIndex = baseName.IndexOf('.');
        if (dotIndex >= 0)
            baseName = baseName.Substring(0, dotIndex);
        return _WindowsReservedNames.Contains(baseName);
    }

    /// <summary>
    /// Validates the format and safety of a multipart upload identifier.
    /// </summary>
    internal static void ValidateUploadId(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId) ||
            uploadId.Length > 128 ||
            uploadId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            uploadId.Contains('/') || uploadId.Contains('\\') ||
            uploadId.Contains(".."))
        {
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));
        }
    }

    /// <summary>
    /// Resolves the filesystem path to the staging directory for the specified upload ID.
    /// </summary>
    internal string GetStagingDirectory(string uploadId)
    {
        ValidateUploadId(uploadId);
        string stagingDir = Path.GetFullPath(Path.Combine(_UploadsDirectory, uploadId));
        string uploadsWithSep = _UploadsDirectory + Path.DirectorySeparatorChar;
        if (!stagingDir.StartsWith(uploadsWithSep, StringComparison.OrdinalIgnoreCase) ||
            stagingDir.Length <= uploadsWithSep.Length)
        {
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));
        }
        return stagingDir;
    }

    /// <summary>
    /// Resolves and validates the filesystem path for the specified bucket name.
    /// </summary>
    internal string GetBucketPath(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket) ||
            bucket.StartsWith(".") ||
            bucket.EndsWith(".") ||
            bucket.Contains('/') || bucket.Contains('\\') ||
            bucket.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            IsWindowsReservedName(bucket))
        {
            throw new S3Exception(new Error(ErrorCode.InvalidBucketName));
        }

        return Path.Combine(_RootDirectory, bucket);
    }

    /// <summary>
    /// Resolves and validates the filesystem path for an object in the specified bucket.
    /// </summary>
    internal string GetObjectPath(string bucket, string? key)
    {
        key ??= string.Empty;

        if (key == "." || key == "..")
            throw new S3Exception(new Error(ErrorCode.InvalidRequest));

        string bPath = GetBucketPath(bucket);
        if (!Directory.Exists(bPath)) throw new S3Exception(new Error(ErrorCode.NoSuchBucket));

        // Disallow direct access to internal metadata files
        string trimmedKey = key.Trim('/', '\\');
        if (trimmedKey.Equals(".s3bucket.xml", StringComparison.OrdinalIgnoreCase) ||
            trimmedKey.EndsWith(".s3meta.xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new S3Exception(new Error(ErrorCode.AccessDenied, "Access to internal metadata files is prohibited."));
        }

        // Disallow NTFS Alternate Data Streams (colons)
        if (key.IndexOf(':') >= 0)
        {
            throw new S3Exception(new Error(ErrorCode.InvalidRequest, "Colons are not permitted in object keys."));
        }

        string[] segments = key.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (!string.IsNullOrEmpty(segment) && IsWindowsReservedName(segment))
            {
                throw new S3Exception(new Error(ErrorCode.InvalidRequest, "The specified key contains a reserved device name."));
            }
        }

        string normalizedKey = key.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(bPath, normalizedKey));

        // Path traversal defense
        string bPathWithSep = bPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(bPathWithSep, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.Equals(bPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new S3Exception(new Error(ErrorCode.InvalidRequest));
        }

        return fullPath;
    }

    private string GetMetaPath(string objectPath) => objectPath + ".s3meta.xml";
    private string GetBucketMetaPath(string bPath) => Path.Combine(bPath, ".s3bucket.xml");

    /// <summary>
    /// Reads and deserializes bucket metadata from disk.
    /// </summary>
    internal BucketMetadata ReadBucketMeta(string bPath)
    {
        string metaPath = GetBucketMetaPath(bPath);
        if (!File.Exists(metaPath))
            return new BucketMetadata();
        try
        {
            string xml = File.ReadAllText(metaPath);
            return SerializationHelper.DeserializeXml<BucketMetadata>(xml);
        }
        catch
        {
            return new BucketMetadata();
        }
    }

    /// <summary>
    /// Serializes and writes bucket metadata to disk.
    /// </summary>
    internal void WriteBucketMeta(string bPath, BucketMetadata meta)
    {
        string metaPath = GetBucketMetaPath(bPath);
        try
        {
            string xml = SerializationHelper.SerializeXml(meta, false);
            File.WriteAllText(metaPath, xml);
        }
        catch
        {
        }
    }

    private static bool IsDirectoryKey(string? key) =>
        key != null && (key.EndsWith("/") || key.EndsWith("\\"));

    private static string GetTargetPath(string path, string? key) =>
        path.TrimEnd(Path.DirectorySeparatorChar, '/', '\\');

    /// <summary>
    /// Checks whether the specified directory is empty, ignoring internal S3 metadata files.
    /// </summary>
    internal static bool IsEmptyDirectory(string dirPath)
    {
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dirPath))
            {
                if (!entry.EndsWith(".s3meta.xml", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(entry).Equals(".s3bucket.xml", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether the specified directory should be included in object listings.
    /// </summary>
    internal bool IsIncludedDirectory(string dirPath)
    {
        if (Path.GetFileName(dirPath).StartsWith(".", StringComparison.Ordinal))
            return false;

        return IsEmptyDirectory(dirPath) || File.Exists(GetMetaPath(dirPath));
    }

    private bool ObjectExists(string path, string key)
    {
        if (IsDirectoryKey(key) || (!File.Exists(path) && Directory.Exists(path)))
        {
            string dirPath = GetTargetPath(path, key);
            return Directory.Exists(dirPath);
        }
        return File.Exists(path);
    }

    private string EnsureBucketExists(string bucket)
    {
        string bPath = GetBucketPath(bucket);
        if (!Directory.Exists(bPath))
            throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
        return bPath;
    }

    private (string Path, string TargetPath) EnsureObjectExists(string bucket, string key)
    {
        string path = GetObjectPath(bucket, key);
        if (!ObjectExists(path, key))
            throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        return (path, GetTargetPath(path, key));
    }

    private static void PopulateUserMetadataHeaders(Dictionary<string, string>? userMetadata, S3ObjectPayload payload)
    {
        if (userMetadata == null) return;
        foreach (var kvp in userMetadata)
        {
            string headerKey = kvp.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? kvp.Key
                : "x-amz-meta-" + kvp.Key;
            payload.Headers[headerKey] = kvp.Value;
        }
    }

    #region Service

    /// <inheritdoc />
    public Task<ListAllMyBucketsResult> ListBucketsAsync(IS3Request request, CancellationToken cancellationToken = default)
    {
        var dirs = Directory.GetDirectories(_RootDirectory)
            .Select(d => Path.GetFileName(d))
            .Where(name => !name.StartsWith("."))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new Bucket(n, Directory.GetCreationTimeUtc(Path.Combine(_RootDirectory, n))))
            .ToList();

        var result = new ListAllMyBucketsResult
        {
            Owner = DefaultOwner,
            Buckets = new Buckets(dirs)
        };

        return Task.FromResult(result);
    }

    #endregion

    #region Bucket

    /// <inheritdoc />
    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string path = GetBucketPath(bucket);
        return Task.FromResult(Directory.Exists(path));
    }

    /// <inheritdoc />
    public Task CreateBucketAsync(string bucket, CreateBucketConfiguration? configuration, CancellationToken cancellationToken = default)
    {
        string path = GetBucketPath(bucket);
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string path = EnsureBucketExists(bucket);

        // Check if there are any actual object files or directory marker objects
        bool hasFiles = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Any(f => !f.EndsWith(".s3meta.xml", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals(".s3bucket.xml", StringComparison.OrdinalIgnoreCase));
        if (hasFiles)
            throw new S3Exception(new Error(ErrorCode.BucketNotEmpty));

        bool hasDirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Any(IsIncludedDirectory);
        if (hasDirs)
            throw new S3Exception(new Error(ErrorCode.BucketNotEmpty));

        Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default)
    {
        EnsureBucketExists(bucket);
        return Task.FromResult(new LocationConstraint("us-east-1"));
    }

    /// <inheritdoc />
    public Task<ListBucketResult> ListObjectsAsync(string bucket, S3Request request, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);

        bool isV2 = request.RetrieveQueryValue("list-type") == "2";
        string prefix = request.Prefix ?? "";
        string? delimiter = request.Delimiter;
        string? after = isV2 ? (request.ContinuationToken ?? request.StartAfter) : request.Marker;
        int max = Math.Min(Math.Max(1, request.MaxKeys), 1000);

        var r = new ListBucketResult
        {
            Name = bucket,
            Prefix = prefix,
            Delimiter = delimiter,
            MaxKeys = max,
            Marker = isV2 ? null : request.Marker,
            ContinuationToken = isV2 ? request.ContinuationToken : null,
            StartAfter = isV2 ? request.StartAfter : null
        };

        var filesList = Directory.EnumerateFiles(bPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".s3meta.xml", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals(".s3bucket.xml", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Substring(bPath.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/'));

        var dirsList = Directory.EnumerateDirectories(bPath, "*", SearchOption.AllDirectories)
            .Where(IsIncludedDirectory)
            .Select(d => d.Substring(bPath.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/') + "/");

        var allFiles = filesList
            .Concat(dirsList)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        string? last = null;
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in allFiles)
        {
            string entry = key;
            bool isPrefix = false;

            if (!string.IsNullOrEmpty(delimiter))
            {
                int i = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
                if (i >= 0)
                {
                    entry = key.Substring(0, i + delimiter.Length);
                    isPrefix = true;
                }
            }

            if (after != null && string.CompareOrdinal(entry, after) <= 0) continue;
            if (isPrefix && seenPrefixes.Contains(entry)) continue;

            if (r.Contents.Count + seenPrefixes.Count >= max)
            {
                r.IsTruncated = true;
                break;
            }

            if (isPrefix)
            {
                seenPrefixes.Add(entry);
                r.CommonPrefixes.Add(new CommonPrefixes(entry));
            }
            else
            {
                string objPath = GetObjectPath(bucket, key);
                DateTime lastModified;
                long length;
                string etag;
                if (IsDirectoryKey(key))
                {
                    string dirPath = GetTargetPath(objPath, key);
                    var di = new DirectoryInfo(dirPath);
                    var meta = ReadMeta(dirPath);
                    lastModified = meta?.LastModified ?? di.LastWriteTimeUtc;
                    length = 0;
                    etag = meta?.ETag ?? "\"d41d8cd98f00b204e9800998ecf8427e\"";
                }
                else
                {
                    var fi = new FileInfo(objPath);
                    var meta = ReadMeta(objPath);
                    lastModified = meta?.LastModified ?? fi.LastWriteTimeUtc;
                    length = fi.Length;
                    etag = meta?.ETag ?? "\"\"";
                }
                r.Contents.Add(new ObjectMetadata(key, lastModified, etag, length, DefaultOwner));
            }

            last = entry;
        }

        if (r.IsTruncated)
        {
            if (isV2) r.NextContinuationToken = last;
            else r.NextMarker = last;
        }

        if (isV2) r.KeyCount = r.Contents.Count + r.CommonPrefixes.Count;

        return Task.FromResult(r);
    }

    /// <inheritdoc />
    public Task<ListVersionsResult> ListObjectVersionsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);

        var r = new ListVersionsResult { Name = bucket, MaxKeys = 1000 };
        var allFiles = Directory.GetFiles(bPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".s3meta.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var f in allFiles)
        {
            string key = f.Substring(bPath.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
            var fi = new FileInfo(f);
            var meta = ReadMeta(f);
            r.Versions.Add(new ObjectVersion(key, "null", true, meta?.LastModified ?? fi.LastWriteTimeUtc, meta?.ETag ?? "\"\"", fi.Length, DefaultOwner));
        }

        return Task.FromResult(r);
    }

    /// <inheritdoc />
    public Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucketPath(bucket);
        var r = new ListMultipartUploadsResult { Bucket = bucket, MaxUploads = 1000 };

        foreach (var u in _ActiveUploads.Values.Where(u => u.Bucket == bucket).OrderBy(u => u.Key, StringComparer.Ordinal))
        {
            r.Uploads.Add(new Upload
            {
                Key = u.Key,
                UploadId = u.UploadId,
                Owner = DefaultOwner,
                Initiator = DefaultOwner,
                StorageClass = StorageClassEnum.STANDARD,
                Initiated = u.Initiated
            });
        }

        return Task.FromResult(r);
    }

    /// <inheritdoc />
    public Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        return Task.FromResult(meta.Acl ?? new AccessControlPolicy(DefaultOwner, new AccessControlList()));
    }

    /// <inheritdoc />
    public Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Acl = acl;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Acl = null;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        if (meta.Tags != null) return Task.FromResult(meta.Tags);
        throw new S3Exception(new Error(ErrorCode.NoSuchTagSet));
    }

    /// <inheritdoc />
    public Task SetBucketTagsAsync(string bucket, Tagging tags, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Tags = tags;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Tags = null;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        return Task.FromResult(meta.Versioning ?? new VersioningConfiguration());
    }

    /// <inheritdoc />
    public Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Versioning = configuration;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        if (meta.Website != null) return Task.FromResult(meta.Website);
        throw new S3Exception(new Error(ErrorCode.NoSuchWebsiteConfiguration));
    }

    /// <inheritdoc />
    public Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Website = configuration;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Website = null;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        return Task.FromResult(meta.Logging ?? new BucketLoggingStatus());
    }

    /// <inheritdoc />
    public Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus logging, CancellationToken cancellationToken = default)
    {
        string bPath = EnsureBucketExists(bucket);
        var meta = ReadBucketMeta(bPath);
        meta.Logging = logging;
        WriteBucketMeta(bPath, meta);
        return Task.CompletedTask;
    }

    #endregion

    #region Object

    /// <inheritdoc />
    public Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        string path = GetObjectPath(bucket, key);
        bool isDir = IsDirectoryKey(key) || (!File.Exists(path) && Directory.Exists(path));
        if (isDir)
        {
            string dirPath = GetTargetPath(path, key);
            if (!Directory.Exists(dirPath)) return Task.FromResult<ObjectMetadata?>(null);

            var di = new DirectoryInfo(dirPath);
            var meta = ReadMeta(dirPath);

            var md = new ObjectMetadata(key ?? string.Empty, meta?.LastModified ?? di.LastWriteTimeUtc, meta?.ETag ?? "\"d41d8cd98f00b204e9800998ecf8427e\"", 0, DefaultOwner)
            {
                ContentType = meta?.ContentType ?? "application/x-directory"
            };
            if (meta?.UserMetadata != null)
            {
                foreach (var kvp in meta.UserMetadata)
                {
                    md.Metadata[kvp.Key] = kvp.Value;
                }
            }
            return Task.FromResult<ObjectMetadata?>(md);
        }

        if (!File.Exists(path)) return Task.FromResult<ObjectMetadata?>(null);

        var fi = new FileInfo(path);
        var fileMeta = ReadMeta(path);

        var fileMd = new ObjectMetadata(key, fileMeta?.LastModified ?? fi.LastWriteTimeUtc, fileMeta?.ETag ?? "\"\"", fi.Length, DefaultOwner)
        {
            ContentType = fileMeta?.ContentType ?? "binary/octet-stream"
        };
        if (fileMeta?.UserMetadata != null)
        {
            foreach (var kvp in fileMeta.UserMetadata)
            {
                fileMd.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return Task.FromResult<ObjectMetadata?>(fileMd);
    }

    /// <inheritdoc />
    public Task<S3ObjectPayload> GetObjectAsync(string bucket, string key, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default)
    {
        string path = GetObjectPath(bucket, key);
        bool isDir = IsDirectoryKey(key) || (!File.Exists(path) && Directory.Exists(path));
        if (isDir)
        {
            string dirPath = GetTargetPath(path, key);
            if (!Directory.Exists(dirPath)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));

            var di = new DirectoryInfo(dirPath);
            var dirMeta = ReadMeta(dirPath);

            var payload = new S3ObjectPayload
            {
                Stream = Stream.Null,
                ContentLength = 0,
                ContentType = dirMeta?.ContentType ?? "application/x-directory",
                ETag = dirMeta?.ETag ?? "\"d41d8cd98f00b204e9800998ecf8427e\"",
                LastModified = dirMeta?.LastModified ?? di.LastWriteTimeUtc,
                TotalSize = 0
            };

            PopulateUserMetadataHeaders(dirMeta?.UserMetadata, payload);

            return Task.FromResult(payload);
        }

        if (!File.Exists(path)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));

        var fi = new FileInfo(path);
        long total = fi.Length;
        var meta = ReadMeta(path);

        var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);

        if (rangeStart != null || rangeEnd != null)
        {
            long start = rangeStart ?? 0;
            long end = rangeEnd ?? (total - 1);
            if (end >= total) end = total - 1;
            if (start > end || start >= total)
            {
                fileStream.Dispose();
                throw new S3Exception(new Error(ErrorCode.InvalidRange));
            }

            long length = end - start + 1;
            var subStream = new SubStream(fileStream, start, length);

            var payload = new S3ObjectPayload
            {
                Stream = subStream,
                ContentLength = length,
                ContentType = meta?.ContentType ?? "binary/octet-stream",
                ETag = meta?.ETag,
                LastModified = meta?.LastModified ?? fi.LastWriteTimeUtc,
                TotalSize = total,
                RangeStart = start,
                RangeEnd = end
            };

            PopulateUserMetadataHeaders(meta?.UserMetadata, payload);

            return Task.FromResult(payload);
        }
        else
        {
            var payload = new S3ObjectPayload
            {
                Stream = fileStream,
                ContentLength = total,
                ContentType = meta?.ContentType ?? "binary/octet-stream",
                ETag = meta?.ETag,
                LastModified = meta?.LastModified ?? fi.LastWriteTimeUtc,
                TotalSize = total
            };

            PopulateUserMetadataHeaders(meta?.UserMetadata, payload);

            return Task.FromResult(payload);
        }
    }

    /// <summary>
    /// Streams data to a file on disk while calculating its MD5 ETag concurrently.
    /// </summary>
    internal async Task<string> WriteStreamToFileWithEtagAsync(string filePath, Stream data, long contentLength, CancellationToken cancellationToken)
    {
        string etag;
        _ActiveWrites[filePath] = new ActiveWriteState { FilePath = filePath };
        try
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 81920, useAsync: true))
            using (var md5 = MD5.Create())
            using (var cryptoStream = new CryptoStream(fs, md5, CryptoStreamMode.Write))
            {
                if (contentLength > 0)
                {
                    byte[] buffer = new byte[81920];
                    long remaining = contentLength;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int n = await data.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
                        if (n <= 0)
                            break;
                        await cryptoStream.WriteAsync(buffer, 0, n, cancellationToken).ConfigureAwait(false);
                        remaining -= n;
                    }
                }
                else
                {
                    await data.CopyToAsync(cryptoStream, 81920, cancellationToken).ConfigureAwait(false);
                }

                cryptoStream.FlushFinalBlock();
                etag = "\"" + BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant() + "\"";
            }
        }
        catch
        {
            try
            {
                if (FileDeleter != null)
                {
                    FileDeleter(filePath);
                }
                else if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
            throw;
        }
        finally
        {
            _ActiveWrites.TryRemove(filePath, out _);
        }

        return etag;
    }

    /// <inheritdoc />
    public async Task<string> PutObjectAsync(
        string bucket,
        string key,
        Stream data,
        long contentLength,
        string? contentType,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        string path = GetObjectPath(bucket, key);

        if (IsDirectoryKey(key))
        {
            string dirPath = GetTargetPath(path, key);
            Directory.CreateDirectory(dirPath);

            if (contentLength > 0 && data != Stream.Null)
            {
                await data.CopyToAsync(Stream.Null, 81920, cancellationToken).ConfigureAwait(false);
            }

            string dirEtag = "\"d41d8cd98f00b204e9800998ecf8427e\"";
            var dirMeta = new FileMetadata
            {
                ContentType = string.IsNullOrEmpty(contentType) ? "application/x-directory" : contentType,
                ETag = dirEtag,
                LastModified = DateTime.UtcNow,
                UserMetadata = metadata ?? new Dictionary<string, string>()
            };

            WriteMeta(dirPath, dirMeta);
            return dirEtag;
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string etag = await WriteStreamToFileWithEtagAsync(path, data, contentLength, cancellationToken).ConfigureAwait(false);

        var meta = new FileMetadata
        {
            ContentType = string.IsNullOrEmpty(contentType) ? "binary/octet-stream" : contentType,
            ETag = etag,
            LastModified = DateTime.UtcNow,
            UserMetadata = metadata ?? new Dictionary<string, string>()
        };

        WriteMeta(path, meta);
        return etag;
    }

    /// <inheritdoc />
    public Task DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        string path = GetObjectPath(bucket, key);
        if (IsDirectoryKey(key))
        {
            string dirPath = GetTargetPath(path, key);
            if (Directory.Exists(dirPath))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dirPath).Any())
                    {
                        DeleteEmptyDirectory(dirPath);
                    }
                }
                catch (Exception)
                {
                }
            }

            string dirMetaPath = GetMetaPath(dirPath);
            DeleteFileIfExists(dirMetaPath);
        }
        else
        {
            DeleteFileIfExists(path);

            string metaPath = GetMetaPath(path);
            DeleteFileIfExists(metaPath);
        }

        PruneEmptyParentDirectories(bucket, path, key);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Recursively deletes empty parent directories up to the bucket root after object deletion.
    /// </summary>
    internal void PruneEmptyParentDirectories(string bucket, string path, string key)
    {
        string bPath = GetBucketPath(bucket);
        string? current = Path.GetDirectoryName(GetTargetPath(path, key));
        while (current != null && current.Length > bPath.Length && Directory.Exists(current))
        {
            if (!Directory.EnumerateFileSystemEntries(current).Any())
            {
                try
                {
                    DeleteEmptyDirectory(current);
                }
                catch (Exception)
                {
                    if (Directory.Exists(current))
                    {
                        break;
                    }
                }

                current = Path.GetDirectoryName(current);
            }
            else
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<DeleteResult> DeleteMultipleObjectsAsync(string bucket, DeleteMultiple request, CancellationToken cancellationToken = default)
    {
        var result = new DeleteResult();
        if (request?.Objects != null)
        {
            foreach (var item in request.Objects)
            {
                if (!string.IsNullOrEmpty(item.Key))
                {
                    try
                    {
                        await DeleteObjectAsync(bucket, item.Key, item.VersionId, cancellationToken).ConfigureAwait(false);
                        result.DeletedObjects.Add(new Deleted(item.Key, "null", false));
                    }
                    catch (Exception ex)
                    {
                        var err = new Error(ex is S3Exception s3ex ? s3ex.Error.Code : ErrorCode.InternalError, ex.Message)
                        {
                            Key = item.Key,
                            VersionId = item.VersionId
                        };
                        result.Errors.Add(err);
                    }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task<CopyObjectResult> CopyObjectAsync(string srcBucket, string srcKey, string destBucket, string destKey, CancellationToken cancellationToken = default)
    {
        string srcPath = GetObjectPath(srcBucket, srcKey);
        if (!ObjectExists(srcPath, srcKey)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));

        string destPath = GetObjectPath(destBucket, destKey);
        if (IsDirectoryKey(destKey))
        {
            string destDirPath = GetTargetPath(destPath, destKey);
            Directory.CreateDirectory(destDirPath);
            string srcTargetPath = GetTargetPath(srcPath, srcKey);
            var srcDirMeta = ReadMeta(srcTargetPath);
            var destDirMeta = new FileMetadata
            {
                ContentType = srcDirMeta?.ContentType ?? "application/x-directory",
                ETag = srcDirMeta?.ETag ?? "\"d41d8cd98f00b204e9800998ecf8427e\"",
                LastModified = DateTime.UtcNow
            };
            WriteMeta(destDirPath, destDirMeta);
            return Task.FromResult(new CopyObjectResult(destDirMeta.ETag, destDirMeta.LastModified));
        }

        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        if (!string.Equals(srcPath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(srcPath, destPath, overwrite: true);
        }

        var srcMeta = ReadMeta(srcPath);
        var destMeta = new FileMetadata
        {
            ContentType = srcMeta?.ContentType ?? "binary/octet-stream",
            ETag = srcMeta?.ETag ?? "\"\"",
            LastModified = DateTime.UtcNow
        };
        WriteMeta(destPath, destMeta);

        var result = new CopyObjectResult(destMeta.ETag, destMeta.LastModified);
        return Task.FromResult(result);
    }

    #endregion

    #region Multipart-Uploads

    private UploadState? GetUploadState(string uploadId, string stagingDir)
    {
        if (_ActiveUploads.TryGetValue(uploadId, out var upload))
            return upload;

        string infoPath = Path.Combine(stagingDir, ".uploadinfo");
        if (File.Exists(infoPath))
        {
            try
            {
                var lines = File.ReadAllLines(infoPath);
                if (lines.Length >= 2)
                {
                    string rawBucket = lines[0].Trim();
                    string rawKey = lines[1].Trim();
                    var recovered = new UploadState
                    {
                        UploadId = uploadId,
                        Bucket = Uri.UnescapeDataString(rawBucket),
                        Key = Uri.UnescapeDataString(rawKey)
                    };
                    _ActiveUploads[uploadId] = recovered;
                    return recovered;
                }
            }
            catch { }
        }

        return null;
    }

    /// <inheritdoc />
    public Task<InitiateMultipartUploadResult> CreateMultipartUploadAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        GetBucketPath(bucket);
        string uploadId = Guid.NewGuid().ToString("N");
        string stagingDir = GetStagingDirectory(uploadId);
        Directory.CreateDirectory(stagingDir);

        var upload = new UploadState
        {
            UploadId = uploadId,
            Bucket = bucket,
            Key = key,
            Initiated = DateTime.UtcNow
        };

        _ActiveUploads[uploadId] = upload;
        File.WriteAllText(Path.Combine(stagingDir, ".uploadinfo"), $"{Uri.EscapeDataString(bucket)}\n{Uri.EscapeDataString(key)}\n{DateTime.UtcNow:O}");

        var result = new InitiateMultipartUploadResult(bucket, key, uploadId);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream data, long contentLength, CancellationToken cancellationToken = default)
    {
        if (partNumber < 1 || partNumber > 10000)
            throw new S3Exception(new Error(ErrorCode.InvalidArgument, "Part number must be an integer between 1 and 10000."));

        string stagingDir = GetStagingDirectory(uploadId);
        if (!Directory.Exists(stagingDir)) throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        var upload = GetUploadState(uploadId, stagingDir);
        if (upload != null && (!string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) || !string.Equals(upload.Key, key, StringComparison.Ordinal)))
        {
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));
        }

        string partPath = Path.Combine(stagingDir, $"{partNumber}.part");

        string etag = await WriteStreamToFileWithEtagAsync(partPath, data, contentLength, cancellationToken).ConfigureAwait(false);

        File.WriteAllText(partPath + ".etag", etag);
        return etag;
    }

    /// <inheritdoc />
    public async Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, CompleteMultipartUpload parts, CancellationToken cancellationToken = default)
    {
        string stagingDir = GetStagingDirectory(uploadId);
        if (!Directory.Exists(stagingDir)) throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        var upload = GetUploadState(uploadId, stagingDir);
        if (upload != null && (!string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) || !string.Equals(upload.Key, key, StringComparison.Ordinal)))
        {
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));
        }

        string destPath = GetObjectPath(bucket, key);
        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        List<string> partFiles;
        if (parts?.Parts != null && parts.Parts.Count > 0)
        {
            partFiles = new List<string>(parts.Parts.Count);
            int lastPartNum = 0;
            foreach (var p in parts.Parts)
            {
                if (p.PartNumber <= lastPartNum || p.PartNumber < 1 || p.PartNumber > 10000)
                {
                    throw new S3Exception(new Error(ErrorCode.InvalidPartOrder, "Parts must be in ascending order."));
                }
                lastPartNum = p.PartNumber;

                string pf = Path.Combine(stagingDir, $"{p.PartNumber}.part");
                if (!File.Exists(pf))
                {
                    throw new S3Exception(new Error(ErrorCode.InvalidPart, $"Part {p.PartNumber} does not exist."));
                }

                if (ValidatePartETags && !string.IsNullOrEmpty(p.ETag))
                {
                    string etagPath = pf + ".etag";
                    string recordedEtag = File.Exists(etagPath) ? File.ReadAllText(etagPath).Trim() : "\"\"";
                    if (!string.Equals(recordedEtag.Trim('"'), p.ETag.Trim('"'), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new S3Exception(new Error(ErrorCode.InvalidPart, $"Part {p.PartNumber} ETag does not match."));
                    }
                }

                partFiles.Add(pf);
            }
        }
        else
        {
            partFiles = Directory.GetFiles(stagingDir, "*.part")
                .Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out _))
                .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
                .ToList();
        }

        var partDigests = new List<byte[]>(partFiles.Count);
        using (var destFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            foreach (var pf in partFiles)
            {
                string etagPath = pf + ".etag";
                string recordedEtag = File.Exists(etagPath) ? File.ReadAllText(etagPath).Trim() : string.Empty;
                byte[] partDigest = ETagHelper.HexToBytes(recordedEtag);

                using var partFs = new FileStream(pf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
                using var partMd5 = partDigest.Length == 16 ? null : MD5.Create();

                while (true)
                {
                    int n = await partFs.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (n <= 0) break;
                    await destFs.WriteAsync(buffer, 0, n, cancellationToken).ConfigureAwait(false);
                    partMd5?.TransformBlock(buffer, 0, n, null, 0);
                }

                if (partDigest.Length != 16 && partMd5 != null)
                {
                    partMd5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    partDigest = partMd5.Hash!;
                }

                partDigests.Add(partDigest);
            }
        }

        string etag = ETagHelper.ComputeMultipartETag(partDigests);

        var meta = new FileMetadata
        {
            ContentType = "binary/octet-stream",
            ETag = etag,
            LastModified = DateTime.UtcNow
        };
        WriteMeta(destPath, meta);

        DeleteStagingDirectory(stagingDir);
        _ActiveUploads.TryRemove(uploadId, out _);

        var result = new CompleteMultipartUploadResult
        {
            Bucket = bucket,
            Key = key,
            ETag = etag,
            Location = "/" + bucket + "/" + key
        };

        return result;
    }

    /// <inheritdoc />
    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        string stagingDir = GetStagingDirectory(uploadId);
        if (Directory.Exists(stagingDir))
        {
            var upload = GetUploadState(uploadId, stagingDir);
            if (upload != null && (!string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) || !string.Equals(upload.Key, key, StringComparison.Ordinal)))
            {
                throw new S3Exception(new Error(ErrorCode.NoSuchUpload));
            }
            DeleteStagingDirectory(stagingDir);
        }
        _ActiveUploads.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ListPartsResult> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        string stagingDir = GetStagingDirectory(uploadId);
        if (!Directory.Exists(stagingDir)) throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        var upload = GetUploadState(uploadId, stagingDir);
        if (upload != null && (!string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) || !string.Equals(upload.Key, key, StringComparison.Ordinal)))
        {
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));
        }

        var partFiles = Directory.GetFiles(stagingDir, "*.part")
            .Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out _))
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
            .ToList();

        var partsList = new List<Part>();
        foreach (var pf in partFiles)
        {
            int num = int.Parse(Path.GetFileNameWithoutExtension(pf));
            var fi = new FileInfo(pf);
            string etagPath = pf + ".etag";
            string etag = File.Exists(etagPath) ? File.ReadAllText(etagPath).Trim() : "\"\"";
            partsList.Add(new Part
            {
                PartNumber = num,
                ETag = etag,
                Size = (int)fi.Length,
                LastModified = fi.LastWriteTimeUtc
            });
        }

        var result = new ListPartsResult
        {
            Bucket = bucket,
            Key = key,
            UploadId = uploadId,
            Parts = partsList,
            MaxParts = 1000,
            IsTruncated = false
        };

        return Task.FromResult(result);
    }

    #endregion

    #region Metadata-and-Locking

    /// <inheritdoc />
    public Task<Tagging> GetObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath);
        return Task.FromResult(meta?.Tags ?? new Tagging());
    }

    /// <inheritdoc />
    public Task SetObjectTagsAsync(string bucket, string key, Tagging tags, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath) ?? new FileMetadata();
        meta.Tags = tags;
        WriteMeta(targetPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath);
        if (meta != null)
        {
            meta.Tags = null;
            WriteMeta(targetPath, meta);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LegalHold> GetLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath);
        return Task.FromResult(meta?.LegalHold ?? new LegalHold("OFF"));
    }

    /// <inheritdoc />
    public Task SetLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath) ?? new FileMetadata();
        meta.LegalHold = legalHold;
        WriteMeta(targetPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Retention> GetRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath);
        return Task.FromResult(meta?.Retention ?? new Retention());
    }

    /// <inheritdoc />
    public Task SetRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath) ?? new FileMetadata();
        meta.Retention = retention;
        WriteMeta(targetPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AccessControlPolicy> GetObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath);
        return Task.FromResult(meta?.Acl ?? new AccessControlPolicy(DefaultOwner, new AccessControlList()));
    }

    /// <inheritdoc />
    public Task SetObjectAclAsync(string bucket, string key, AccessControlPolicy acl, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath) ?? new FileMetadata();
        meta.Acl = acl;
        WriteMeta(targetPath, meta);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var (_, targetPath) = EnsureObjectExists(bucket, key);
        var meta = ReadMeta(targetPath);
        if (meta != null)
        {
            meta.Acl = null;
            WriteMeta(targetPath, meta);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<CopyPartResult> UploadPartCopyAsync(string srcBucket, string srcKey, string destBucket, string destKey, string uploadId, int partNumber, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default)
    {
        string srcPath = GetObjectPath(srcBucket, srcKey);
        if (!File.Exists(srcPath)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));

        var fi = new FileInfo(srcPath);
        long srcLength = fi.Length;
        long start = rangeStart ?? 0;
        long end = rangeEnd ?? (srcLength - 1);
        if (start < 0 || start > end || start >= srcLength)
            throw new S3Exception(new Error(ErrorCode.InvalidRange));
        if (end >= srcLength) end = srcLength - 1;

        long length = end - start + 1;
        using var fs = File.OpenRead(srcPath);
        using var subStream = new SubStream(fs, start, length);

        string etag = await UploadPartAsync(destBucket, destKey, uploadId, partNumber, subStream, length, cancellationToken).ConfigureAwait(false);
        return new CopyPartResult(etag, DateTime.UtcNow);
    }

    /// <inheritdoc />
    public Task<RestoreObjectResult> RestoreObjectAsync(string bucket, string key, RestoreRequest request, CancellationToken cancellationToken = default)
    {
        EnsureObjectExists(bucket, key);
        return Task.FromResult(new RestoreObjectResult { AlreadyRestored = false });
    }

    #endregion

    /// <summary>
    /// Reads and deserializes object file metadata from disk.
    /// </summary>
    internal FileMetadata? ReadMeta(string objectPath)
    {
        string metaPath = GetMetaPath(objectPath);
        if (!File.Exists(metaPath)) return null;

        try
        {
            string xml = File.ReadAllText(metaPath);
            return SerializationHelper.DeserializeXml<FileMetadata>(xml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes and writes object file metadata to disk.
    /// </summary>
    internal void WriteMeta(string objectPath, FileMetadata meta)
    {
        string metaPath = GetMetaPath(objectPath);
        try
        {
            string xml = SerializationHelper.SerializeXml(meta, false);
            File.WriteAllText(metaPath, xml);
        }
        catch
        {
        }
    }
}
