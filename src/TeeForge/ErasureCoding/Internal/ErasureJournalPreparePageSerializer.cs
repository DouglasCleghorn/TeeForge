using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureJournalPreparePageSerializer
{
    private const int PrepareHashOffset = 128;
    private const int PrepareHashLength = 16;

    internal static UInt128 Write(
        in ErasureJournalPreparePage value,
        ReadOnlySpan<ErasureJournalRange> ranges,
        ReadOnlySpan<byte> localPayload,
        int shardSize,
        Span<byte> destination)
    {
        Validate(value, ranges, shardSize);
        if (!ValidateLocalPayload(value, localPayload))
        {
            throw new ArgumentException("The local payload does not match its persisted length and hash.", nameof(localPayload));
        }

        if (destination.Length < ErasureFormatV1.PageSize)
        {
            throw new ArgumentException("A complete 4096-byte journal-prepare destination is required.", nameof(destination));
        }

        Span<byte> page = destination[..ErasureFormatV1.PageSize];
        page.Clear();
        ErasureFormatV1.JournalPrepareMagic.CopyTo(page);
        BinaryPrimitives.WriteUInt16LittleEndian(page[8..], ErasureFormatV1.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(page[10..], ErasureFormatV1.PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(page[12..], value.TransactionFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(page[16..], value.TransactionSequence);
        WriteGuid(page[24..40], value.TransactionId);
        WriteGuid(page[40..56], value.SetId);
        WriteGuid(page[56..72], value.ConfigurationId);
        BinaryPrimitives.WriteUInt64LittleEndian(page[72..], value.ConfigurationGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(page[80..], value.StripeIndex);
        WriteGuid(page[88..104], value.StripeGenerationId);
        BinaryPrimitives.WriteUInt16LittleEndian(page[104..], value.MemberPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(page[106..], (ushort)ranges.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(page[108..], value.LocalPayloadLength);
        BinaryPrimitives.WriteUInt128LittleEndian(page[112..], value.LocalPayloadHash);
        BinaryPrimitives.WriteUInt16LittleEndian(page[144..], ErasureFormatV1.JournalRangeDescriptorOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(page[146..], ErasureFormatV1.JournalRangeDescriptorSize);

        for (int index = 0; index < ranges.Length; index++)
        {
            WriteRange(
                page[(ErasureFormatV1.JournalRangeDescriptorOffset + index * ErasureFormatV1.JournalRangeDescriptorSize)..],
                ranges[index]);
        }

        UInt128 hash = XxHash128.HashToUInt128(page);
        BinaryPrimitives.WriteUInt128LittleEndian(page[PrepareHashOffset..], hash);
        return hash;
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> source,
        int shardSize,
        out ErasureJournalPreparePage value,
        out ErasureJournalRange[] ranges,
        out UInt128 preparePageHash)
    {
        value = default;
        ranges = [];
        preparePageHash = 0;
        if (source.Length < ErasureFormatV1.PageSize)
        {
            return false;
        }

        ReadOnlySpan<byte> page = source[..ErasureFormatV1.PageSize];
        if (!page[..8].SequenceEqual(ErasureFormatV1.JournalPrepareMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[8..]) != ErasureFormatV1.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[10..]) != ErasureFormatV1.PageSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[144..]) != ErasureFormatV1.JournalRangeDescriptorOffset ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[146..]) != ErasureFormatV1.JournalRangeDescriptorSize)
        {
            return false;
        }

        UInt128 encodedHash = BinaryPrimitives.ReadUInt128LittleEndian(page[PrepareHashOffset..]);
        if (encodedHash != ErasureFormatHash.ComputeWithClearedField(page, PrepareHashOffset, PrepareHashLength))
        {
            return false;
        }

        ushort rangeCount = BinaryPrimitives.ReadUInt16LittleEndian(page[106..]);
        if (rangeCount > ErasureFormatV1.MaximumJournalRangeCount)
        {
            return false;
        }

        var candidate = new ErasureJournalPreparePage(
            BinaryPrimitives.ReadUInt32LittleEndian(page[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[16..]),
            ReadGuid(page[24..40]),
            ReadGuid(page[40..56]),
            ReadGuid(page[56..72]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[72..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[80..]),
            ReadGuid(page[88..104]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[104..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[108..]),
            BinaryPrimitives.ReadUInt128LittleEndian(page[112..]));
        var candidateRanges = new ErasureJournalRange[rangeCount];
        for (int index = 0; index < candidateRanges.Length; index++)
        {
            candidateRanges[index] = ReadRange(
                page[(ErasureFormatV1.JournalRangeDescriptorOffset + index * ErasureFormatV1.JournalRangeDescriptorSize)..]);
        }

        try
        {
            Validate(candidate, candidateRanges, shardSize);
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
        ranges = candidateRanges;
        preparePageHash = encodedHash;
        return true;
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> localPayload,
        int shardSize,
        out ErasureJournalPreparePage value,
        out ErasureJournalRange[] ranges,
        out UInt128 preparePageHash) =>
        TryRead(source, shardSize, out value, out ranges, out preparePageHash) &&
        ValidateLocalPayload(value, localPayload);

    internal static UInt128 ComputeLocalPayloadHash(ReadOnlySpan<byte> localPayload) =>
        XxHash128.HashToUInt128(localPayload);

    internal static bool ValidateLocalPayload(
        in ErasureJournalPreparePage value,
        ReadOnlySpan<byte> localPayload) =>
        value.LocalPayloadLength == localPayload.Length &&
        value.LocalPayloadHash == ComputeLocalPayloadHash(localPayload);

    private static void Validate(
        in ErasureJournalPreparePage value,
        ReadOnlySpan<ErasureJournalRange> ranges,
        int shardSize)
    {
        if (shardSize is < ErasureFormatV1.MinimumShardSize or > ErasureFormatV1.MaximumShardSize ||
            !int.IsPow2(shardSize))
        {
            throw new ArgumentOutOfRangeException(nameof(shardSize));
        }

        if (value.TransactionId == Guid.Empty ||
            value.SetId == Guid.Empty ||
            value.ConfigurationId == Guid.Empty ||
            value.StripeGenerationId == Guid.Empty)
        {
            throw new ArgumentException("Journal transaction identifiers cannot be empty.", nameof(value));
        }

        if (value.MemberPosition >= ErasureFormatV1.MaximumMemberCount ||
            value.LocalPayloadLength > shardSize ||
            ranges.Length > ErasureFormatV1.MaximumJournalRangeCount)
        {
            throw new ArgumentException("The journal prepare-page geometry is invalid.", nameof(value));
        }

        uint expectedPayloadOffset = 0;
        uint previousShardEnd = 0;
        for (int index = 0; index < ranges.Length; index++)
        {
            ErasureJournalRange range = ranges[index];
            uint shardEnd = checked(range.ShardOffset + range.Length);
            uint payloadEnd = checked(range.PayloadOffset + range.Length);
            if (range.Flags != 0 ||
                range.Length == 0 ||
                range.ShardOffset % ErasureFormatV1.IntegrityBlockSize != 0 ||
                range.Length % ErasureFormatV1.IntegrityBlockSize != 0 ||
                range.PayloadOffset != expectedPayloadOffset ||
                (index > 0 && range.ShardOffset < previousShardEnd) ||
                shardEnd > shardSize ||
                payloadEnd > value.LocalPayloadLength)
            {
                throw new ArgumentException("Journal ranges must be aligned, ordered, nonoverlapping, and densely packed.", nameof(ranges));
            }

            expectedPayloadOffset = payloadEnd;
            previousShardEnd = shardEnd;
        }

        if (expectedPayloadOffset != value.LocalPayloadLength)
        {
            throw new ArgumentException("Journal ranges must describe the complete local payload.", nameof(ranges));
        }
    }

    private static void WriteRange(Span<byte> destination, in ErasureJournalRange value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value.ShardOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], value.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], value.PayloadOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], value.Flags);
    }

    private static ErasureJournalRange ReadRange(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(source),
        BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[12..]));

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int bytesWritten) || bytesWritten != 16)
        {
            throw new InvalidOperationException("A UUID must occupy exactly 16 bytes.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}
