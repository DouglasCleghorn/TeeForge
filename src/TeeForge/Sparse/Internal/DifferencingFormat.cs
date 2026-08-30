using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.Sparse.Internal;

internal static class DifferencingFormat
{
    internal const int SectorSize = 4096;
    internal const int IdentifierOffset = 0;
    internal const int RootAOffset = SectorSize;
    internal const int RootBOffset = SectorSize * 2;
    internal const int ParentHintOffset = 160;
    internal const int StateHeaderSize = 64;
    internal const int RegistryHeaderSize = 64;
    internal const ushort MajorVersion = 1;
    internal const ushort MinorVersion = 0;

    private static ReadOnlySpan<byte> IdentifierSignature => "TeeDIF\r\n"u8;
    private static ReadOnlySpan<byte> RootSignature => "TeeDRoot"u8;
    private static ReadOnlySpan<byte> StateSignature => "TeeDSt\r\n"u8;
    private static ReadOnlySpan<byte> RegistrySignature => "TeeDDep\n"u8;

    internal static void WriteIdentifier(
        Span<byte> destination,
        DifferencingIdentity identity,
        ReadOnlySpan<byte> parentHint)
    {
        if (destination.Length != SectorSize || parentHint.Length > SectorSize - ParentHintOffset)
        {
            throw new ArgumentException("Invalid differencing identifier data.");
        }

        destination.Clear();
        IdentifierSignature.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[18..], MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)identity.BlockSize);
        WriteGuid(destination[24..40], identity.Id);
        WriteGuid(destination[40..56], identity.InitialDataWriteId);
        WriteGuid(destination[56..72], identity.BaseId);
        WriteGuid(destination[72..88], identity.BaseDataWriteId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[88..], identity.VirtualCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[96..], (uint)parentHint.Length);
        parentHint.CopyTo(destination[ParentHintOffset..]);
        WriteChecksum(destination);
    }

    internal static bool TryReadIdentifier(
        ReadOnlySpan<byte> source,
        out DifferencingIdentity identity,
        out byte[] parentHint)
    {
        identity = default;
        parentHint = [];
        if (source.Length != SectorSize || !source[..8].SequenceEqual(IdentifierSignature) || !ValidateChecksum(source))
        {
            return false;
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[16..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[18..]);
        int blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[20..]));
        Guid id = ReadGuid(source[24..40]);
        Guid dataWriteId = ReadGuid(source[40..56]);
        Guid baseId = ReadGuid(source[56..72]);
        Guid baseDataWriteId = ReadGuid(source[72..88]);
        long capacity = BinaryPrimitives.ReadInt64LittleEndian(source[88..]);
        int hintLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[96..]));
        if (major == 0 || !DynamicAllocationFormat.IsValidBlockSize(blockSize) ||
            !DynamicAllocationFormat.IsValidVirtualCapacity(capacity) ||
            id == Guid.Empty || dataWriteId == Guid.Empty || baseId == Guid.Empty ||
            baseDataWriteId == Guid.Empty || hintLength < 0 || hintLength > SectorSize - ParentHintOffset ||
            source[100..ParentHintOffset].IndexOfAnyExcept((byte)0) >= 0 ||
            source[(ParentHintOffset + hintLength)..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        identity = new(id, dataWriteId, baseId, baseDataWriteId, major, minor, blockSize, capacity);
        parentHint = source.Slice(ParentHintOffset, hintLength).ToArray();
        return true;
    }

    internal static void WriteRoot(Span<byte> destination, DifferencingRoot root)
    {
        if (destination.Length != SectorSize)
        {
            throw new ArgumentException("A differencing root is exactly 4 KiB.", nameof(destination));
        }

        destination.Clear();
        RootSignature.CopyTo(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], root.Generation);
        WriteGuid(destination[24..40], root.Id);
        WriteGuid(destination[40..56], root.DataWriteId);
        WriteGuid(destination[56..72], root.BaseId);
        WriteGuid(destination[72..88], root.BaseDataWriteId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[88..], root.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[90..], root.MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[92..], (uint)root.BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(destination[96..], root.VirtualCapacity);
        BinaryPrimitives.WriteInt64LittleEndian(destination[104..], root.LogicalLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[112..], root.StateTailOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[120..], root.RegistryTailOffset);
        WriteChecksum(destination);
    }

    internal static bool TryReadRoot(ReadOnlySpan<byte> source, out DifferencingRoot root)
    {
        root = default;
        if (source.Length != SectorSize || !source[..8].SequenceEqual(RootSignature) || !ValidateChecksum(source))
        {
            return false;
        }

        root = new(
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
            ReadGuid(source[24..40]),
            ReadGuid(source[40..56]),
            ReadGuid(source[56..72]),
            ReadGuid(source[72..88]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[88..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[90..]),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[92..])),
            BinaryPrimitives.ReadInt64LittleEndian(source[96..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[104..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[112..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[120..]));

        return root.Generation > 0 && root.Id != Guid.Empty && root.DataWriteId != Guid.Empty &&
            root.BaseId != Guid.Empty && root.BaseDataWriteId != Guid.Empty &&
            DynamicAllocationFormat.IsValidBlockSize(root.BlockSize) &&
            DynamicAllocationFormat.IsValidVirtualCapacity(root.VirtualCapacity) &&
            root.LogicalLength >= 0 && root.LogicalLength <= root.VirtualCapacity &&
            IsOptionalBlockOffset(root.StateTailOffset, root.BlockSize) &&
            IsOptionalBlockOffset(root.RegistryTailOffset, root.BlockSize) &&
            source[128..].IndexOfAnyExcept((byte)0) < 0;
    }

    internal static void WriteStateRecord(Span<byte> destination, DifferenceBlockRecord record)
    {
        int presenceLength = GetPresenceByteCount(destination.Length);
        if (destination.Length < DynamicAllocationFormat.MinimumBlockSize || record.Presence.Length != presenceLength)
        {
            throw new ArgumentException("Invalid differencing state record.", nameof(record));
        }

        destination.Clear();
        StateSignature.CopyTo(destination);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], record.PreviousOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], record.LogicalBlock);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], record.BatValue);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], (uint)presenceLength);
        record.Presence.CopyTo(destination[StateHeaderSize..]);
        WriteChecksum(destination);
    }

    internal static bool TryReadStateRecord(ReadOnlySpan<byte> source, out DifferenceBlockRecord record)
    {
        record = default;
        int presenceLength = GetPresenceByteCount(source.Length);
        if (source.Length < DynamicAllocationFormat.MinimumBlockSize ||
            !source[..8].SequenceEqual(StateSignature) || !ValidateChecksum(source) ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[40..]) != (uint)presenceLength ||
            source[44..StateHeaderSize].IndexOfAnyExcept((byte)0) >= 0 ||
            source[(StateHeaderSize + presenceLength)..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        record = new(
            BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[32..]),
            source.Slice(StateHeaderSize, presenceLength).ToArray());
        return record.PreviousOffset >= 0 && record.LogicalBlock >= 0;
    }

    internal static void WriteRegistryRecord(
        Span<byte> destination,
        long previousOffset,
        Guid id,
        bool registered)
    {
        destination.Clear();
        RegistrySignature.CopyTo(destination);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], previousOffset);
        WriteGuid(destination[24..40], id);
        destination[40] = registered ? (byte)1 : (byte)0;
        WriteChecksum(destination);
    }

    internal static bool TryReadRegistryRecord(
        ReadOnlySpan<byte> source,
        out long previousOffset,
        out Guid id,
        out bool registered)
    {
        previousOffset = 0;
        id = Guid.Empty;
        registered = false;
        if (source.Length < DynamicAllocationFormat.MinimumBlockSize ||
            !source[..8].SequenceEqual(RegistrySignature) || !ValidateChecksum(source) ||
            source[40] > 1 || source[41..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        previousOffset = BinaryPrimitives.ReadInt64LittleEndian(source[16..]);
        id = ReadGuid(source[24..40]);
        registered = source[40] != 0;
        return previousOffset >= 0 && id != Guid.Empty;
    }

    internal static int GetPresenceByteCount(int blockSize) =>
        (GetGrainCount(blockSize) + 7) / 8;

    internal static int GetGrainCount(int blockSize) => blockSize / SectorSize;

    internal static long ComposeBatValue(long payloadOffset, DifferenceBlockState state) =>
        payloadOffset | (long)state;

    internal static DifferenceBlockState GetBatState(long batValue) =>
        (DifferenceBlockState)(batValue & 7);

    internal static long GetBatPayloadOffset(long batValue) => batValue & ~7L;

    internal static bool IsValidBatValue(long batValue, int blockSize)
    {
        DifferenceBlockState state = GetBatState(batValue);
        long offset = GetBatPayloadOffset(batValue);
        return state switch
        {
            DifferenceBlockState.Inherited or DifferenceBlockState.Erased => offset == 0,
            DifferenceBlockState.FullyPresent or DifferenceBlockState.PartiallyPresent =>
                offset >= blockSize && (offset & (blockSize - 1L)) == 0,
            _ => false,
        };
    }

    internal static long AlignUp(long value, int alignment) =>
        DynamicAllocationFormat.AlignUp(value, alignment);

    private static bool IsOptionalBlockOffset(long value, int blockSize) =>
        value == 0 || (value >= blockSize && (value & (blockSize - 1L)) == 0);

    private static void WriteChecksum(Span<byte> structure)
    {
        structure.Slice(8, 8).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(structure[8..], ComputeChecksum(structure));
    }

    private static bool ValidateChecksum(ReadOnlySpan<byte> structure) =>
        BinaryPrimitives.ReadUInt64LittleEndian(structure[8..]) == ComputeChecksum(structure);

    private static ulong ComputeChecksum(ReadOnlySpan<byte> structure)
    {
        var hasher = new XxHash64();
        hasher.Append(structure[..8]);
        Span<byte> zero = stackalloc byte[8];
        zero.Clear();
        hasher.Append(zero);
        hasher.Append(structure[16..]);
        Span<byte> hash = stackalloc byte[8];
        hasher.GetCurrentHash(hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("Could not encode a GUID.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}

internal enum DifferenceBlockState : long
{
    Inherited = 0,
    Erased = 2,
    FullyPresent = 6,
    PartiallyPresent = 7,
}

internal readonly record struct DifferencingIdentity(
    Guid Id,
    Guid InitialDataWriteId,
    Guid BaseId,
    Guid BaseDataWriteId,
    ushort MajorVersion,
    ushort MinorVersion,
    int BlockSize,
    long VirtualCapacity);

internal readonly record struct DifferencingRoot(
    ulong Generation,
    Guid Id,
    Guid DataWriteId,
    Guid BaseId,
    Guid BaseDataWriteId,
    ushort MajorVersion,
    ushort MinorVersion,
    int BlockSize,
    long VirtualCapacity,
    long LogicalLength,
    long StateTailOffset,
    long RegistryTailOffset);

internal readonly record struct DifferenceBlockRecord(
    long PreviousOffset,
    long LogicalBlock,
    long BatValue,
    byte[] Presence);
