using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal static class ErasureMemberSuperblockSerializer
{
    private const int HashOffset = 192;
    private const int HashLength = 16;
    private const int DefinedLength = 208;

    internal static void Write(in ErasureMemberSuperblock value, Span<byte> destination)
    {
        Validate(value);
        if (destination.Length < ErasureFormatV1.PageSize)
        {
            throw new ArgumentException("A complete 4096-byte superblock destination is required.", nameof(destination));
        }

        Span<byte> page = destination[..ErasureFormatV1.PageSize];
        page.Clear();
        ErasureFormatV1.MemberMagic.CopyTo(page);
        BinaryPrimitives.WriteUInt16LittleEndian(page[8..], ErasureFormatV1.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(page[10..], ErasureFormatV1.PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(page[12..], value.FeatureFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(page[16..], value.SuperblockGeneration);
        WriteGuid(page[24..40], value.SetId);
        WriteGuid(page[40..56], value.MemberId);
        WriteGuid(page[56..72], value.ConfigurationId);
        BinaryPrimitives.WriteUInt64LittleEndian(page[72..], value.ConfigurationGeneration);
        BinaryPrimitives.WriteUInt16LittleEndian(page[80..], value.MemberPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(page[82..], value.DataShardCount);
        BinaryPrimitives.WriteUInt16LittleEndian(page[84..], value.ParityShardCount);
        BinaryPrimitives.WriteUInt16LittleEndian(page[86..], value.JournalSlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(page[88..], value.ShardSize);
        BinaryPrimitives.WriteUInt32LittleEndian(page[92..], value.IntegrityBlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(page[96..], value.StripeCount);
        BinaryPrimitives.WriteUInt64LittleEndian(page[104..], value.LogicalCapacity);
        BinaryPrimitives.WriteUInt64LittleEndian(page[112..], value.MetadataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(page[120..], value.MetadataLength);
        BinaryPrimitives.WriteUInt64LittleEndian(page[128..], value.JournalOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(page[136..], value.JournalLength);
        BinaryPrimitives.WriteUInt64LittleEndian(page[144..], value.DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(page[152..], value.ShardHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(page[156..], value.ShardRecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(page[160..], value.ConfigurationRecordOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(page[168..], value.ConfigurationRecordLength);
        BinaryPrimitives.WriteUInt32LittleEndian(page[172..], value.MemberStateFlags);
        BinaryPrimitives.WriteUInt128LittleEndian(page[176..], value.ConfigurationRecordHash);

        UInt128 hash = XxHash128.HashToUInt128(page);
        BinaryPrimitives.WriteUInt128LittleEndian(page[HashOffset..], hash);
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, out ErasureMemberSuperblock value)
    {
        value = default;
        if (source.Length < ErasureFormatV1.PageSize)
        {
            return false;
        }

        ReadOnlySpan<byte> page = source[..ErasureFormatV1.PageSize];
        if (!page[..8].SequenceEqual(ErasureFormatV1.MemberMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[8..]) != ErasureFormatV1.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[10..]) != ErasureFormatV1.PageSize)
        {
            return false;
        }

        UInt128 storedHash = BinaryPrimitives.ReadUInt128LittleEndian(page[HashOffset..]);
        Span<byte> hashInput = stackalloc byte[ErasureFormatV1.PageSize];
        page.CopyTo(hashInput);
        hashInput.Slice(HashOffset, HashLength).Clear();
        if (storedHash != XxHash128.HashToUInt128(hashInput))
        {
            return false;
        }

        var candidate = new ErasureMemberSuperblock(
            BinaryPrimitives.ReadUInt32LittleEndian(page[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[16..]),
            ReadGuid(page[24..40]),
            ReadGuid(page[40..56]),
            ReadGuid(page[56..72]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[72..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[80..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[82..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[84..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[86..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[88..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[92..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[96..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[104..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[112..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[120..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[128..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[136..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[144..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[152..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[156..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[160..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[168..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[172..]),
            BinaryPrimitives.ReadUInt128LittleEndian(page[176..]));

        try
        {
            Validate(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    internal static bool TrySelect(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        out ErasureMemberSuperblock value)
    {
        bool firstValid = TryRead(first, out ErasureMemberSuperblock firstValue);
        bool secondValid = TryRead(second, out ErasureMemberSuperblock secondValue);

        if (!firstValid)
        {
            value = secondValue;
            return secondValid;
        }

        if (!secondValid)
        {
            value = firstValue;
            return true;
        }

        if (firstValue.SuperblockGeneration == secondValue.SuperblockGeneration && firstValue != secondValue)
        {
            value = default;
            return false;
        }

        value = secondValue.SuperblockGeneration > firstValue.SuperblockGeneration ? secondValue : firstValue;
        return true;
    }

    private static void Validate(in ErasureMemberSuperblock value)
    {
        if (value.SetId == Guid.Empty)
        {
            throw new ArgumentException("The erasure-set identifier cannot be empty.", nameof(value));
        }

        if (value.MemberId == Guid.Empty)
        {
            throw new ArgumentException("The member identifier cannot be empty.", nameof(value));
        }

        if (value.ConfigurationId == Guid.Empty)
        {
            throw new ArgumentException("The configuration identifier cannot be empty.", nameof(value));
        }

        int readQuorum = ErasureFormatV1.CalculateReadQuorum(value.DataShardCount, value.ParityShardCount);
        int memberCount = checked(value.DataShardCount + value.ParityShardCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value.MemberPosition, memberCount);
        if (readQuorum != value.DataShardCount || value.IntegrityBlockSize != ErasureFormatV1.IntegrityBlockSize)
        {
            throw new ArgumentException("The member geometry is not supported by format version 1.", nameof(value));
        }

        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            checked((int)value.ShardSize),
            checked((long)value.StripeCount),
            checked((long)value.MetadataLength),
            value.JournalSlotCount);
        long logicalCapacity = ErasureFormatV1.CalculateLogicalCapacity(
            value.DataShardCount,
            value.ParityShardCount,
            checked((int)value.ShardSize),
            checked((long)value.StripeCount));

        if (value.LogicalCapacity != (ulong)logicalCapacity ||
            value.MetadataOffset != (ulong)layout.MetadataOffset ||
            value.JournalOffset != (ulong)layout.JournalOffset ||
            value.JournalLength != (ulong)layout.JournalLength ||
            value.DataOffset != (ulong)layout.DataOffset ||
            value.ShardHeaderSize != ErasureFormatV1.ShardHeaderSize ||
            value.ShardRecordSize != layout.ShardRecordSize)
        {
            throw new ArgumentException("The persisted offsets do not match the member geometry.", nameof(value));
        }

        ulong metadataEnd = checked(value.MetadataOffset + value.MetadataLength);
        ulong configurationEnd = checked(value.ConfigurationRecordOffset + value.ConfigurationRecordLength);
        if (value.ConfigurationRecordLength < ErasureFormatV1.PageSize ||
            value.ConfigurationRecordLength % ErasureFormatV1.PageSize != 0 ||
            value.ConfigurationRecordOffset < value.MetadataOffset ||
            configurationEnd > metadataEnd)
        {
            throw new ArgumentException("The stable configuration record must fit the metadata region.", nameof(value));
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
