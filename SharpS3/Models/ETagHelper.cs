namespace SharpS3.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;

    /// <summary>
    /// Helper methods for ETag normalization and formatting.
    /// </summary>
    internal static class ETagHelper
    {
        /// <summary>
        /// Normalizes an ETag by trimming whitespace and ensuring it is enclosed in double quotes.
        /// If the input is null or empty, it is returned as-is.
        /// </summary>
        /// <param name="value">The raw ETag string.</param>
        /// <returns>Quoted ETag or the original value if null or empty.</returns>
        public static string? FormatETag(string? value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return value;
            }

            value = value!.Trim();

            if (value.Length == 0)
            {
                return String.Empty;
            }

            if (!value.StartsWith("\"", StringComparison.Ordinal))
            {
                value = "\"" + value;
            }

            if (!value.EndsWith("\"", StringComparison.Ordinal) || value.Length == 1)
            {
                value = value + "\"";
            }

            return value;
        }

        /// <summary>
        /// Computes the composite ETag for a completed multipart upload according to the Amazon S3 specification:
        /// MD5(part1_md5_bytes || part2_md5_bytes || ... || partN_md5_bytes) + "-" + partCount.
        /// </summary>
        public static string ComputeMultipartETag(IEnumerable<byte[]> partDigests)
        {
            var list = partDigests as IList<byte[]> ?? partDigests.ToList();
            byte[] concatenated = new byte[list.Count * 16];
            for (int i = 0; i < list.Count; i++)
            {
                byte[] d = list[i];
                if (d != null && d.Length == 16)
                {
                    Buffer.BlockCopy(d, 0, concatenated, i * 16, 16);
                }
            }

            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(concatenated);
            return "\"" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + "-" + list.Count + "\"";
        }

        /// <summary>
        /// Parses a hex-encoded MD5 string into 16 raw bytes.
        /// </summary>
        public static byte[] HexToBytes(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
            hex = hex!.Trim().Trim('"');
            if (hex.Length != 32) return Array.Empty<byte>();

            try
            {
                byte[] bytes = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                return bytes;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }
}
