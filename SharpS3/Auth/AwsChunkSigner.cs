namespace SharpS3.Auth;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Computes and validates AWS Signature Version 4 streaming (chunked) signatures.
/// Each chunk's signature is chained from the previous signature (or the seed signature for the first chunk).
/// </summary>
public class AwsChunkSigner : IDisposable
{
    private const string EmptySha256Hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _Timestamp;
    private readonly string _Scope;
    private readonly byte[] _SigningKey;
    private string _PreviousSignature;
    private bool _Disposed;

    /// <summary>
    /// Gets the current chained signature.
    /// </summary>
    public string CurrentSignature
    {
        get
        {
            ThrowIfDisposed();
            return _PreviousSignature;
        }
    }

    /// <summary>
    /// Gets the timestamp used for signing.
    /// </summary>
    public string Timestamp => _Timestamp;

    /// <summary>
    /// Gets the credential scope used for signing.
    /// </summary>
    public string Scope => _Scope;

    /// <summary>
    /// Initializes a new instance of the <see cref="AwsChunkSigner"/> class.
    /// </summary>
    /// <param name="timestamp">Request timestamp in yyyyMMddTHHmmssZ or yyyyMMdd format.</param>
    /// <param name="region">AWS region.</param>
    /// <param name="service">AWS service.</param>
    /// <param name="signingKey">Derived signing key bytes.</param>
    /// <param name="seedSignature">Seed signature from the Authorization header.</param>
    public AwsChunkSigner(string timestamp, string region, string service, byte[] signingKey, string seedSignature)
    {
        if (string.IsNullOrEmpty(timestamp))
        {
            throw new ArgumentNullException(nameof(timestamp));
        }

        if (string.IsNullOrEmpty(region))
        {
            throw new ArgumentNullException(nameof(region));
        }

        if (string.IsNullOrEmpty(service))
        {
            throw new ArgumentNullException(nameof(service));
        }

        if (signingKey == null)
        {
            throw new ArgumentNullException(nameof(signingKey));
        }

        if (string.IsNullOrEmpty(seedSignature))
        {
            throw new ArgumentNullException(nameof(seedSignature));
        }

        _SigningKey = signingKey;
        _PreviousSignature = seedSignature;

        _Timestamp = timestamp;
        string dateStamp = timestamp.Length >= 8 ? timestamp.Substring(0, 8) : timestamp;
        _Scope = $"{dateStamp}/{region}/{service}/aws4_request";
    }

    /// <summary>
    /// Computes the expected signature for the next chunk and advances the signature chain.
    /// </summary>
    /// <param name="chunkData">Chunk data bytes. Empty or null for the final zero-length chunk.</param>
    /// <returns>Hex-encoded chunk signature.</returns>
    public string ComputeChunkSignature(byte[]? chunkData)
    {
        ThrowIfDisposed();

        string chunkHash;
        if (chunkData == null || chunkData.Length == 0)
        {
            chunkHash = EmptySha256Hex;
        }
        else
        {
            using var sha = SHA256.Create();
            chunkHash = BytesToHex(sha.ComputeHash(chunkData));
        }

        string stringToSign = $"AWS4-HMAC-SHA256-PAYLOAD\n{_Timestamp}\n{_Scope}\n{_PreviousSignature}\n{EmptySha256Hex}\n{chunkHash}";
        string signature = ComputeHmacHex(_SigningKey, stringToSign);
        _PreviousSignature = signature;
        return signature;
    }

    /// <summary>
    /// Validates a chunk's signature and advances the signature chain on success.
    /// </summary>
    /// <param name="chunkData">Chunk data bytes. Empty or null for the final zero-length chunk.</param>
    /// <param name="providedSignature">Signature provided in the chunk header.</param>
    /// <returns>True if signature matches; otherwise false.</returns>
    public bool ValidateChunk(byte[]? chunkData, string providedSignature)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(providedSignature))
        {
            return false;
        }

        string previousSignature = _PreviousSignature;
        string expected = ComputeChunkSignature(chunkData);
        if (ConstantTimeHelper.FixedTimeEqualsHex(expected, providedSignature))
        {
            return true;
        }

        _PreviousSignature = previousSignature;
        return false;
    }

    /// <summary>
    /// Computes the expected signature for the trailing headers.
    /// </summary>
    /// <param name="trailerHeaders">Trailing headers as key-value pairs.</param>
    /// <returns>Hex-encoded trailer signature.</returns>
    public string ComputeTrailerSignature(SortedDictionary<string, string> trailerHeaders)
    {
        ThrowIfDisposed();

        if (trailerHeaders == null)
        {
            throw new ArgumentNullException(nameof(trailerHeaders));
        }

        string trailerHash;
        if (trailerHeaders.Count == 0)
        {
            trailerHash = EmptySha256Hex;
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var kvp in trailerHeaders)
            {
                sb.Append(kvp.Key.ToLowerInvariant()).Append(':').Append(kvp.Value.Trim()).Append('\n');
            }

            using var sha = SHA256.Create();
            trailerHash = BytesToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        string stringToSign = $"AWS4-HMAC-SHA256-TRAILER\n{_Timestamp}\n{_Scope}\n{_PreviousSignature}\n{trailerHash}";
        string signature = ComputeHmacHex(_SigningKey, stringToSign);
        _PreviousSignature = signature;
        return signature;
    }

    /// <summary>
    /// Validates the trailer signature and advances the signature chain on success.
    /// </summary>
    /// <param name="trailerHeaders">Trailing headers as key-value pairs.</param>
    /// <param name="providedSignature">Signature provided in the trailer chunk.</param>
    /// <returns>True if signature matches; otherwise false.</returns>
    public bool ValidateTrailer(SortedDictionary<string, string> trailerHeaders, string providedSignature)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(providedSignature) || trailerHeaders == null)
        {
            return false;
        }

        string previousSignature = _PreviousSignature;
        string expected = ComputeTrailerSignature(trailerHeaders);
        if (ConstantTimeHelper.FixedTimeEqualsHex(expected, providedSignature))
        {
            return true;
        }

        _PreviousSignature = previousSignature;
        return false;
    }

    private static string ComputeHmacHex(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return BytesToHex(hash);
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    private void ThrowIfDisposed()
    {
        if (_Disposed)
        {
            throw new ObjectDisposedException(nameof(AwsChunkSigner));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _Disposed = true;
    }
}
