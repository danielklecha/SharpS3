namespace SharpS3.Auth;

using System;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// Provides constant-time comparison methods to protect against timing attacks on cryptographic signatures.
/// </summary>
internal static class ConstantTimeHelper
{
    /// <summary>
    /// Compares two byte arrays in constant time.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static bool FixedTimeEquals(byte[]? left, byte[]? right)
    {
        if (left == null || right == null)
            return left == right;

        if (left.Length != right.Length)
            return false;

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
#else
        int diff = 0;
        for (int i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }
        return diff == 0;
#endif
    }

    /// <summary>
    /// Compares two hexadecimal strings in constant time (case-insensitive).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static bool FixedTimeEqualsHex(string? left, string? right)
    {
        if (left == null || right == null)
            return left == right;

        if (left.Length != right.Length)
            return false;

        byte[] leftBytes = Encoding.UTF8.GetBytes(left.ToLowerInvariant());
        byte[] rightBytes = Encoding.UTF8.GetBytes(right.ToLowerInvariant());
        return FixedTimeEquals(leftBytes, rightBytes);
    }
}
