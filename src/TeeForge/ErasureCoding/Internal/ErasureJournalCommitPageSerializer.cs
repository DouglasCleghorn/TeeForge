using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureJournalCommitPageSerializer
{
    private const int CommitHashOffset = 136;
    private const int CommitHashLength = 16;

    internal static UInt128 Write(in ErasureJournalCommitPage value, Span<byte> destination)
    {
        Validate(value);
        if (destination.Length < ErasureFormatV1.PageSize)
        {
            throw new ArgumentException("A complete 4096-byte journal-commit destination is required.", nameof(destination));
        }

        Span<byte> page = destination[..ErasureFormatV1.PageSize];
        page.Clear();
        ErasureFormatV1.JournalCommitMagic.CopyTo(page);
        BinaryPrimitives.WriteUInt16LittleEndian(page[8..], ErasureFormatV1.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(page[10..], ErasureFormatV1.PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(page[12..], (uint)value.State);
        BinaryPrimitives.WriteUInt64LittleEndian(page[16..], value.TransactionSequence);
        WriteGuid(page[24..40], value.TransactionId);
        WriteGuid(page[40..56], value.SetId);
        WriteGuid(page[56..72], value.ConfigurationId);
        BinaryPrimitives.WriteUInt64LittleEndian(page[72..], value.StripeIndex);
        WriteGuid(page[80..96], value.StripeGenerationId);
        BinaryPrimitives.WriteUInt16LittleEndian(page[96..], value.MemberPosition);
        BinaryPrimitives.WriteUInt128LittleEndian(page[104..], value.PreparePageHash);
        BinaryPrimitives.WriteUInt128LittleEndian(page[120..], value.LocalPayloadHash);

        UInt128 hash = XxHash128.HashToUInt128(page);
        BinaryPrimitives.WriteUInt128LittleEndian(page[CommitHashOffset..], hash);
        return hash;
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> source,
        out ErasureJournalCommitPage value,
        out UInt128 commitPageHash)
    {
        value = default;
        commitPageHash = 0;
        if (source.Length < ErasureFormatV1.PageSize)
        {
            return false;
        }

        ReadOnlySpan<byte> page = source[..ErasureFormatV1.PageSize];
        if (!page[..8].SequenceEqual(ErasureFormatV1.JournalCommitMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[8..]) != ErasureFormatV1.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[10..]) != ErasureFormatV1.PageSize)
        {
            return false;
        }

        UInt128 encodedHash = BinaryPrimitives.ReadUInt128LittleEndian(page[CommitHashOffset..]);
        if (encodedHash != ErasureFormatHash.ComputeWithClearedField(page, CommitHashOffset, CommitHashLength))
        {
            return false;
        }

        var candidate = new ErasureJournalCommitPage(
            (ErasureJournalCommitState)BinaryPrimitives.ReadUInt32LittleEndian(page[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[16..]),
            ReadGuid(page[24..40]),
            ReadGuid(page[40..56]),
            ReadGuid(page[56..72]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[72..]),
            ReadGuid(page[80..96]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[96..]),
            BinaryPrimitives.ReadUInt128LittleEndian(page[104..]),
            BinaryPrimitives.ReadUInt128LittleEndian(page[120..]));
        try
        {
            Validate(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }

        value = candidate;
        commitPageHash = encodedHash;
        return true;
    }

    internal static bool MatchesPreparePage(
        in ErasureJournalCommitPage commit,
        in ErasureJournalPreparePage prepare,
        UInt128 preparePageHash) =>
        commit.TransactionSequence == prepare.TransactionSequence &&
        commit.TransactionId == prepare.TransactionId &&
        commit.SetId == prepare.SetId &&
        commit.ConfigurationId == prepare.ConfigurationId &&
        commit.StripeIndex == prepare.StripeIndex &&
        commit.StripeGenerationId == prepare.StripeGenerationId &&
        commit.MemberPosition == prepare.MemberPosition &&
        commit.PreparePageHash == preparePageHash &&
        commit.LocalPayloadHash == prepare.LocalPayloadHash;

    private static void Validate(in ErasureJournalCommitPage value)
    {
        if (value.State is not ErasureJournalCommitState.Committed and not ErasureJournalCommitState.Checkpointed)
        {
            throw new ArgumentException("The journal commit state is invalid.", nameof(value));
        }

        if (value.TransactionId == Guid.Empty ||
            value.SetId == Guid.Empty ||
            value.ConfigurationId == Guid.Empty ||
            value.StripeGenerationId == Guid.Empty)
        {
            throw new ArgumentException("Journal transaction identifiers cannot be empty.", nameof(value));
        }

        if (value.MemberPosition >= ErasureFormatV1.MaximumMemberCount)
        {
            throw new ArgumentException("The member position is invalid.", nameof(value));
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
