using System.Buffers;
using System.IO.Hashing;

namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal static class ErasureFormatHash
{
    internal static UInt128 ComputeWithClearedField(
        ReadOnlySpan<byte> source,
        int hashOffset,
        int hashLength = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hashOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hashLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashLength, source.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashOffset, source.Length - hashLength);

        byte[] rented = ArrayPool<byte>.Shared.Rent(source.Length);
        try
        {
            Span<byte> copy = rented.AsSpan(0, source.Length);
            source.CopyTo(copy);
            copy.Slice(hashOffset, hashLength).Clear();
            return XxHash128.HashToUInt128(copy);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static bool IsAllZero(ReadOnlySpan<byte> source)
    {
        foreach (byte value in source)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
