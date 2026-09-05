using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal static class ErasureShardHeaderSerializer
{
    private const int HeaderHashOffset = 80;
    private const int HeaderHashLength = 16;

    internal static UInt128 Write(
        in ErasureShardHeader value,
        ReadOnlySpan<ulong> integrityChecksums,
        Span<byte> destination)
    {
        Validate(value, integrityChecksums.Length);
        if (destination.Length < ErasureFormatV1.ShardHeaderSize)
        {
            throw new ArgumentException("A complete 4096-byte shard-header destination is required.", nameof(destination));
        }

        Span<byte> header = destination[..ErasureFormatV1.ShardHeaderSize];
        header.Clear();
        ErasureFormatV1.ShardMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], ErasureFormatV1.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], ErasureFormatV1.ShardHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], value.ShardFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], value.ConfigurationGeneration);
        WriteGuid(header[24..40], value.ConfigurationId);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], value.StripeIndex);
        WriteGuid(header[48..64], value.StripeGenerationId);
        BinaryPrimitives.WriteUInt16LittleEndian(header[64..], value.MemberPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(header[66..], (ushort)integrityChecksums.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], ErasureFormatV1.IntegrityBlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[72..], value.StoredPayloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[ErasureFormatV1.ShardTransactionSequenceOffset..],
            value.TransactionSequence);

        for (int index = 0; index < integrityChecksums.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                header[(ErasureFormatV1.ShardChecksumOffset + index * sizeof(ulong))..],
                integrityChecksums[index]);
        }

        UInt128 hash = XxHash128.HashToUInt128(header);
        BinaryPrimitives.WriteUInt128LittleEndian(header[HeaderHashOffset..], hash);
        return hash;
    }

    internal static void WriteImplicitZero(Span<byte> destination)
    {
        if (destination.Length < ErasureFormatV1.ShardHeaderSize)
        {
            throw new ArgumentException("A complete 4096-byte shard-header destination is required.", nameof(destination));
        }

        destination[..ErasureFormatV1.ShardHeaderSize].Clear();
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> source,
        out ErasureShardHeader value,
        out ulong[] integrityChecksums,
        out bool isImplicitZero,
        out UInt128 headerHash)
    {
        value = default;
        integrityChecksums = [];
        isImplicitZero = false;
        headerHash = 0;
        if (source.Length < ErasureFormatV1.ShardHeaderSize)
        {
            return false;
        }

        ReadOnlySpan<byte> header = source[..ErasureFormatV1.ShardHeaderSize];
        if (ErasureFormatHash.IsAllZero(header))
        {
            isImplicitZero = true;
            return true;
        }

        if (!header[..8].SequenceEqual(ErasureFormatV1.ShardMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != ErasureFormatV1.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != ErasureFormatV1.ShardHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[68..]) != ErasureFormatV1.IntegrityBlockSize)
        {
            return false;
        }

        UInt128 encodedHash = BinaryPrimitives.ReadUInt128LittleEndian(header[HeaderHashOffset..]);
        if (encodedHash != ErasureFormatHash.ComputeWithClearedField(header, HeaderHashOffset, HeaderHashLength))
        {
            return false;
        }

        ushort checksumCount = BinaryPrimitives.ReadUInt16LittleEndian(header[66..]);
        if (checksumCount > (ErasureFormatV1.ShardHeaderSize - ErasureFormatV1.ShardChecksumOffset) / sizeof(ulong))
        {
            return false;
        }

        var candidate = new ErasureShardHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[16..]),
            ReadGuid(header[24..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[ErasureFormatV1.ShardTransactionSequenceOffset..]),
            ReadGuid(header[48..64]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[64..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[72..]));
        try
        {
            Validate(candidate, checksumCount);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var candidateChecksums = new ulong[checksumCount];
        for (int index = 0; index < candidateChecksums.Length; index++)
        {
            candidateChecksums[index] = BinaryPrimitives.ReadUInt64LittleEndian(
                header[(ErasureFormatV1.ShardChecksumOffset + index * sizeof(ulong))..]);
        }

        value = candidate;
        integrityChecksums = candidateChecksums;
        headerHash = encodedHash;
        return true;
    }

    private static void Validate(in ErasureShardHeader value, int checksumCount)
    {
        if (value.ConfigurationId == Guid.Empty ||
            value.StripeGenerationId == Guid.Empty ||
            value.TransactionSequence == 0)
        {
            throw new ArgumentException("Configuration and stripe-generation identifiers cannot be empty.", nameof(value));
        }

        if (value.MemberPosition >= ErasureFormatV1.MaximumMemberCount ||
            value.StoredPayloadLength is < ErasureFormatV1.MinimumShardSize or > ErasureFormatV1.MaximumShardSize ||
            !int.IsPow2((int)value.StoredPayloadLength) ||
            value.StoredPayloadLength % ErasureFormatV1.IntegrityBlockSize != 0 ||
            checksumCount != value.StoredPayloadLength / ErasureFormatV1.IntegrityBlockSize)
        {
            throw new ArgumentException("The shard header geometry is invalid.", nameof(value));
        }
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int bytesWritten) || bytesWritten != 16)
        {
            throw new InvalidOperationException("A UUID must occupy exactly 16 bytes.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}
