using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureStableConfigurationSerializer
{
    private const int RecordHashOffset = 144;
    private const int RecordHashLength = 16;

    internal static UInt128 Write(
        in ErasureStableConfiguration value,
        ReadOnlySpan<ErasureMemberDescriptor> members,
        Span<byte> destination)
    {
        Validate(value, members);
        int recordLength = ErasureFormatV1.CalculateStableConfigurationRecordLength(members.Length);
        if (destination.Length < recordLength)
        {
            throw new ArgumentException($"A complete {recordLength}-byte configuration-record destination is required.", nameof(destination));
        }

        Span<byte> record = destination[..recordLength];
        record.Clear();
        ErasureFormatV1.MetadataMagic.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[8..], ErasureFormatV1.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(record[10..], ErasureFormatV1.StableConfigurationRecordType);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)recordLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record[16..], ErasureFormatV1.StableConfigurationRecordVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(record[18..], value.RecordFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(record[20..], ErasureFormatV1.StableConfigurationHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(record[22..], ErasureFormatV1.MemberDescriptorSize);
        BinaryPrimitives.WriteUInt64LittleEndian(record[24..], value.MetadataRecordSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(record[32..], value.ConfigurationGeneration);
        WriteGuid(record[40..56], value.ConfigurationId);
        WriteGuid(record[56..72], value.ParentConfigurationId);
        WriteGuid(record[72..88], value.SetId);
        BinaryPrimitives.WriteUInt32LittleEndian(record[88..], value.ConfigurationFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(record[92..], value.CodecId);
        BinaryPrimitives.WriteUInt16LittleEndian(record[94..], value.DataShardCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record[96..], value.ParityShardCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record[98..], (ushort)members.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[100..], value.ShardSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record[104..], value.IntegrityBlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(record[112..], value.StripeCount);
        BinaryPrimitives.WriteUInt64LittleEndian(record[120..], value.LogicalCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(record[128..], ErasureFormatV1.StableConfigurationHeaderSize);

        for (int index = 0; index < members.Length; index++)
        {
            WriteMemberDescriptor(
                record.Slice(
                    ErasureFormatV1.StableConfigurationHeaderSize + index * ErasureFormatV1.MemberDescriptorSize,
                    ErasureFormatV1.MemberDescriptorSize),
                members[index]);
        }

        UInt128 hash = XxHash128.HashToUInt128(record);
        BinaryPrimitives.WriteUInt128LittleEndian(record[RecordHashOffset..], hash);
        return hash;
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> source,
        out ErasureStableConfiguration value,
        out ErasureMemberDescriptor[] members,
        out int recordLength,
        out UInt128 recordHash)
    {
        value = default;
        members = [];
        recordLength = 0;
        recordHash = 0;
        if (source.Length < ErasureFormatV1.StableConfigurationHeaderSize ||
            !source[..8].SequenceEqual(ErasureFormatV1.MetadataMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[8..]) != ErasureFormatV1.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[10..]) != ErasureFormatV1.StableConfigurationRecordType ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[16..]) != ErasureFormatV1.StableConfigurationRecordVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[20..]) != ErasureFormatV1.StableConfigurationHeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[22..]) != ErasureFormatV1.MemberDescriptorSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[128..]) != ErasureFormatV1.StableConfigurationHeaderSize)
        {
            return false;
        }

        uint encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        ushort memberCount = BinaryPrimitives.ReadUInt16LittleEndian(source[98..]);
        int expectedLength;
        try
        {
            expectedLength = ErasureFormatV1.CalculateStableConfigurationRecordLength(memberCount);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (encodedLength != expectedLength || source.Length < expectedLength)
        {
            return false;
        }

        ReadOnlySpan<byte> record = source[..expectedLength];
        UInt128 encodedHash = BinaryPrimitives.ReadUInt128LittleEndian(record[RecordHashOffset..]);
        if (encodedHash != ErasureFormatHash.ComputeWithClearedField(record, RecordHashOffset, RecordHashLength))
        {
            return false;
        }

        var candidate = new ErasureStableConfiguration(
            BinaryPrimitives.ReadUInt16LittleEndian(record[18..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[32..]),
            ReadGuid(record[40..56]),
            ReadGuid(record[56..72]),
            ReadGuid(record[72..88]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[88..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[92..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[94..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[96..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[100..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[104..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[112..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[120..]));

        var candidateMembers = new ErasureMemberDescriptor[memberCount];
        for (int index = 0; index < candidateMembers.Length; index++)
        {
            candidateMembers[index] = ReadMemberDescriptor(
                record.Slice(
                    ErasureFormatV1.StableConfigurationHeaderSize + index * ErasureFormatV1.MemberDescriptorSize,
                    ErasureFormatV1.MemberDescriptorSize));
        }

        try
        {
            Validate(candidate, candidateMembers);
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
        members = candidateMembers;
        recordLength = expectedLength;
        recordHash = encodedHash;
        return true;
    }

    private static void Validate(
        in ErasureStableConfiguration value,
        ReadOnlySpan<ErasureMemberDescriptor> members)
    {
        if ((value.RecordFlags & ErasureFormatV1.StableConfigurationCriticalFlag) == 0)
        {
            throw new ArgumentException("A stable configuration record must be critical.", nameof(value));
        }

        if (value.ConfigurationId == Guid.Empty || value.SetId == Guid.Empty)
        {
            throw new ArgumentException("Configuration and erasure-set identifiers cannot be empty.", nameof(value));
        }

        if (value.CodecId != ErasureFormatV1.ReedSolomonCodecId)
        {
            throw new ArgumentException("The codec identifier is not supported by format version 1.", nameof(value));
        }

        int memberCount = checked(value.DataShardCount + value.ParityShardCount);
        _ = ErasureFormatV1.CalculateReadQuorum(value.DataShardCount, value.ParityShardCount);
        if (members.Length != memberCount || value.IntegrityBlockSize != ErasureFormatV1.IntegrityBlockSize)
        {
            throw new ArgumentException("The configuration member count or integrity geometry is invalid.", nameof(value));
        }

        long logicalCapacity = ErasureFormatV1.CalculateLogicalCapacity(
            value.DataShardCount,
            value.ParityShardCount,
            checked((int)value.ShardSize),
            checked((long)value.StripeCount));
        if (value.LogicalCapacity != (ulong)logicalCapacity)
        {
            throw new ArgumentException("The logical capacity does not match the configuration geometry.", nameof(value));
        }

        ulong minimumMemberLength = checked(
            value.StripeCount * (ErasureFormatV1.ShardHeaderSize + (ulong)value.ShardSize));
        var memberIds = new HashSet<Guid>();
        for (int index = 0; index < members.Length; index++)
        {
            ErasureMemberDescriptor member = members[index];
            ErasureMemberRole expectedRole = index < value.DataShardCount
                ? ErasureMemberRole.Data
                : ErasureMemberRole.Parity;
            if (member.MemberId == Guid.Empty ||
                !memberIds.Add(member.MemberId) ||
                member.Position != index ||
                member.Role != expectedRole ||
                member.RequiredMemberLength < minimumMemberLength ||
                member.RequiredMemberLength % ErasureFormatV1.PageSize != 0)
            {
                throw new ArgumentException("Member descriptors must be unique, ordered, role-consistent, and large enough.", nameof(members));
            }
        }
    }

    private static void WriteMemberDescriptor(Span<byte> destination, in ErasureMemberDescriptor value)
    {
        WriteGuid(destination[..16], value.MemberId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], value.Position);
        destination[18] = (byte)value.Role;
        destination[19] = value.InitialStateFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], value.FeatureFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], value.RequiredMemberLength);
    }

    private static ErasureMemberDescriptor ReadMemberDescriptor(ReadOnlySpan<byte> source) => new(
        ReadGuid(source[..16]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[16..]),
        (ErasureMemberRole)source[18],
        source[19],
        BinaryPrimitives.ReadUInt32LittleEndian(source[20..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[24..]));

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int bytesWritten) || bytesWritten != 16)
        {
            throw new InvalidOperationException("A UUID must occupy exactly 16 bytes.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}
