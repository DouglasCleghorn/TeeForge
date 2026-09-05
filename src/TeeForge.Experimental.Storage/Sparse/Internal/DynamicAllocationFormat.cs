using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;

namespace TeeForge.Experimental.Storage.Sparse.Internal;

internal static class DynamicAllocationFormat
{
    internal const int SectorSize = 4096;
    internal const int IdentifierOffset = 0;
    internal const int RootAOffset = SectorSize;
    internal const int RootBOffset = SectorSize * 2;
    internal const int PrimaryRegionOffset = SectorSize * 3;
    internal const int RegionHeaderSize = 64;
    internal const int RegionEntrySize = 32;
    internal const int JournalHeaderSize = 64;
    internal const int JournalPatchSize = 16;
    internal const int JournalPatchesPerEntry = (SectorSize - JournalHeaderSize) / JournalPatchSize;
    internal const int DefaultBlockSize = 1024 * 1024;
    internal const int MinimumBlockSize = 64 * 1024;
    internal const int MaximumBlockSize = 256 * 1024 * 1024;
    internal const ushort MajorVersion = 1;
    internal const ushort MinorVersion = 0;
    internal const uint RequiredRegionFlag = 1;
    internal const uint BatRegionKind = 1;
    internal const uint TrimRegionKind = 2;
    internal const uint SubRegionKind = 3;
    internal const uint DependentRegionKind = 4;
    internal const int DependentPageHeaderSize = 64;
    internal const int DependentSlotSize = 16;

    private static ReadOnlySpan<byte> IdentifierSignature => "TeeDAS\r\n"u8;
    private static ReadOnlySpan<byte> RootSignature => "TeeRoot\n"u8;
    private static ReadOnlySpan<byte> RegionSignature => "TeeRegn\n"u8;
    private static ReadOnlySpan<byte> JournalSignature => "TeeLog\r\n"u8;
    private static ReadOnlySpan<byte> DependentSignature => "TeeDeps\n"u8;

    internal static int GetJournalLength(int blockSize) => Math.Clamp(blockSize / 4, 16 * 1024, 64 * 1024);

    internal static int GetJournalOffset(int blockSize) => blockSize - GetJournalLength(blockSize);

    internal static int GetPrimaryRegionCapacity(int blockSize) =>
        ((GetJournalOffset(blockSize) - PrimaryRegionOffset - RegionHeaderSize) / RegionEntrySize) - 1;

    internal static int GetSubRegionCapacity(int blockSize) =>
        ((blockSize - RegionHeaderSize) / RegionEntrySize) - 1;

    internal static bool IsValidBlockSize(int blockSize) =>
        blockSize is >= MinimumBlockSize and <= MaximumBlockSize && BitOperations.IsPow2((uint)blockSize);

    internal static long AlignUp(long value, int alignment)
    {
        long mask = alignment - 1L;
        return checked((value + mask) & ~mask);
    }

    internal static long LogicalBlockEnd(long logicalBlock, int blockSize)
    {
        ulong end = ((ulong)logicalBlock + 1UL) * (uint)blockSize;
        return end >= long.MaxValue ? long.MaxValue : (long)end;
    }

    internal static void WriteIdentifier(
        Span<byte> destination,
        Guid id,
        Guid dataWriteId,
        int blockSize,
        long virtualCapacity)
    {
        if (destination.Length != SectorSize)
        {
            throw new ArgumentException("Identifier destination must be exactly 4 KiB.", nameof(destination));
        }

        destination.Clear();
        IdentifierSignature.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[18..], MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)blockSize);
        WriteGuid(destination[24..40], id);
        WriteGuid(destination[40..56], dataWriteId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..], virtualCapacity);
        WriteChecksum(destination);
    }

    internal static bool TryReadIdentifier(ReadOnlySpan<byte> source, out FormatIdentity identity)
    {
        identity = default;
        if (source.Length != SectorSize || !source[..8].SequenceEqual(IdentifierSignature) || !ValidateChecksum(source))
        {
            return false;
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[16..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[18..]);
        int blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[20..]));
        Guid id = ReadGuid(source[24..40]);
        Guid initialDataWriteId = ReadGuid(source[40..56]);
        long virtualCapacity = BinaryPrimitives.ReadInt64LittleEndian(source[56..]);
        if (major == 0 || !IsValidBlockSize(blockSize) || id == Guid.Empty ||
            initialDataWriteId == Guid.Empty || !IsValidVirtualCapacity(virtualCapacity))
        {
            return false;
        }

        identity = new(id, initialDataWriteId, major, minor, blockSize, virtualCapacity);
        return true;
    }

    internal static void WriteRoot(Span<byte> destination, RootState root)
    {
        if (destination.Length != SectorSize)
        {
            throw new ArgumentException("Root destination must be exactly 4 KiB.", nameof(destination));
        }

        destination.Clear();
        RootSignature.CopyTo(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], root.Generation);
        WriteGuid(destination[24..40], root.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[40..], root.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[42..], root.MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], (uint)root.BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..], root.LogicalLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..], root.JournalOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[64..], (uint)root.JournalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[68..], SectorSize);
        WriteGuid(destination[72..88], root.ActiveLogId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[88..], (uint)root.ActiveLogStartSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[92..], (uint)root.ActiveLogEntryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[96..], root.ActiveLogFirstSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[104..], (uint)root.NextJournalSlot);
        BinaryPrimitives.WriteInt64LittleEndian(destination[112..], root.RequiredPhysicalLength);
        WriteGuid(destination[120..136], root.DataWriteId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[136..], root.VirtualCapacity);
        WriteChecksum(destination);
    }

    internal static bool TryReadRoot(ReadOnlySpan<byte> source, out RootState? root)
    {
        root = null;
        if (source.Length != SectorSize || !source[..8].SequenceEqual(RootSignature) || !ValidateChecksum(source))
        {
            return false;
        }

        ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]);
        Guid id = ReadGuid(source[24..40]);
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(source[40..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(source[42..]);
        int blockSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[44..]));
        long logicalLength = BinaryPrimitives.ReadInt64LittleEndian(source[48..]);
        long journalOffset = BinaryPrimitives.ReadInt64LittleEndian(source[56..]);
        int journalLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[64..]));
        int entrySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[68..]));
        Guid activeLogId = ReadGuid(source[72..88]);
        int activeStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[88..]));
        int activeCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[92..]));
        ulong activeFirstSequence = BinaryPrimitives.ReadUInt64LittleEndian(source[96..]);
        int nextSlot = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[104..]));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(source[108..]);
        long requiredLength = BinaryPrimitives.ReadInt64LittleEndian(source[112..]);
        Guid dataWriteId = ReadGuid(source[120..136]);
        long virtualCapacity = BinaryPrimitives.ReadInt64LittleEndian(source[136..]);

        int expectedJournalLength = IsValidBlockSize(blockSize) ? GetJournalLength(blockSize) : -1;
        int slotCount = journalLength > 0 ? journalLength / SectorSize : 0;
        bool clean = activeLogId == Guid.Empty;
        bool valid = generation > 0 &&
            major > 0 &&
            IsValidBlockSize(blockSize) &&
            id != Guid.Empty &&
            dataWriteId != Guid.Empty &&
            IsValidVirtualCapacity(virtualCapacity) &&
            logicalLength >= 0 &&
            logicalLength <= virtualCapacity &&
            (logicalLength == virtualCapacity || (logicalLength & (blockSize - 1L)) == 0) &&
            journalOffset == GetJournalOffset(blockSize) &&
            journalLength == expectedJournalLength &&
            entrySize == SectorSize &&
            flags == 0 &&
            slotCount >= 4 &&
            nextSlot >= 0 && nextSlot < slotCount &&
            activeStart >= 0 && activeStart < slotCount &&
            activeCount >= 0 && activeCount <= slotCount &&
            (clean
                ? activeCount == 0 && activeFirstSequence == 0 && requiredLength == 0
                : activeCount > 0 && activeFirstSequence > 0 && requiredLength >= blockSize);

        if (!valid)
        {
            return false;
        }

        root = new RootState(
            generation,
            id,
            major,
            minor,
            blockSize,
            logicalLength,
            journalOffset,
            journalLength,
            activeLogId,
            activeStart,
            activeCount,
            activeFirstSequence,
            nextSlot,
            requiredLength,
            dataWriteId,
            virtualCapacity);
        return true;
    }

    internal static bool IsValidVirtualCapacity(long virtualCapacity) =>
        virtualCapacity > 0 && (virtualCapacity & (SectorSize - 1L)) == 0;

    internal static int WriteJournalEntry(
        Span<byte> destination,
        Guid logId,
        ulong sequence,
        int entryIndex,
        int entryCount,
        long requiredPhysicalLength,
        ReadOnlySpan<MetadataPatch> patches)
    {
        if (destination.Length != SectorSize || patches.Length > JournalPatchesPerEntry)
        {
            throw new ArgumentException("Invalid journal entry buffer or patch count.");
        }

        destination.Clear();
        JournalSignature.CopyTo(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], sequence);
        WriteGuid(destination[24..40], logId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], (uint)entryIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], (uint)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[48..], (uint)patches.Length);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..], requiredPhysicalLength);

        int cursor = JournalHeaderSize;
        foreach (MetadataPatch patch in patches)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[cursor..], patch.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(destination[(cursor + 8)..], patch.Value);
            cursor += JournalPatchSize;
        }

        WriteChecksum(destination);
        return cursor;
    }

    internal static bool TryReadJournalEntry(ReadOnlySpan<byte> source, out JournalEntry? entry)
    {
        entry = null;
        if (source.Length != SectorSize || !source[..8].SequenceEqual(JournalSignature) || !ValidateChecksum(source))
        {
            return false;
        }

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(source[16..]);
        Guid logId = ReadGuid(source[24..40]);
        int index = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[40..]));
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[44..]));
        int patchCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[48..]));
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[52..]);
        long requiredLength = BinaryPrimitives.ReadInt64LittleEndian(source[56..]);
        if (sequence == 0 || logId == Guid.Empty || index < 0 || count <= 0 || index >= count ||
            patchCount < 0 || patchCount > JournalPatchesPerEntry || reserved != 0 || requiredLength <= 0)
        {
            return false;
        }

        var patches = new MetadataPatch[patchCount];
        int cursor = JournalHeaderSize;
        for (int i = 0; i < patchCount; i++)
        {
            long offset = BinaryPrimitives.ReadInt64LittleEndian(source[cursor..]);
            long value = BinaryPrimitives.ReadInt64LittleEndian(source[(cursor + 8)..]);
            patches[i] = new(offset, value);
            cursor += JournalPatchSize;
        }

        if (source[cursor..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        entry = new(sequence, logId, index, count, requiredLength, patches);
        return true;
    }

    internal static void WriteRegionPage(Span<byte> destination, RegionPage page)
    {
        int requiredLength = RegionHeaderSize + ((page.Entries.Count + 1) * RegionEntrySize);
        if (destination.Length < requiredLength || page.Entries.Count > page.Capacity)
        {
            throw new ArgumentException("Region page destination is too small.", nameof(destination));
        }

        destination.Clear();
        WriteRegionPageParts(
            destination[..(RegionHeaderSize + (page.Entries.Count * RegionEntrySize))],
            destination[^RegionEntrySize..],
            page);
    }

    internal static void WriteRegionPageParts(Span<byte> prefix, Span<byte> link, RegionPage page)
    {
        int prefixLength = RegionHeaderSize + (page.Entries.Count * RegionEntrySize);
        if (prefix.Length != prefixLength || link.Length != RegionEntrySize || page.Entries.Count > page.Capacity)
        {
            throw new ArgumentException("Region page metadata buffers have invalid lengths.", nameof(prefix));
        }

        prefix.Clear();
        link.Clear();
        RegionSignature.CopyTo(prefix);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[16..], page.TableIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[24..], (uint)page.Entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[28..], (uint)page.Capacity);

        int cursor = RegionHeaderSize;
        foreach (RegionEntry item in page.Entries)
        {
            WriteRegionEntry(prefix[cursor..(cursor + RegionEntrySize)], item);
            cursor += RegionEntrySize;
        }

        if (page.NextOffset != 0)
        {
            WriteRegionEntry(
                link,
                new RegionEntry(SubRegionKind, RequiredRegionFlag, page.TableIndex + 1, page.NextOffset));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(prefix[8..], ComputeRegionChecksum(prefix, link));
    }

    internal static bool TryReadRegionPage(
        ReadOnlySpan<byte> source,
        long expectedTableIndex,
        int expectedCapacity,
        out RegionPage? page)
    {
        page = null;
        if (!TryReadRegionPageHeader(source, expectedTableIndex, expectedCapacity, out int entryCount))
        {
            return false;
        }

        int prefixLength = RegionHeaderSize + (entryCount * RegionEntrySize);
        if (source.Length < prefixLength + RegionEntrySize ||
            !TryReadRegionPageParts(source[..prefixLength], source[^RegionEntrySize..], expectedTableIndex, expectedCapacity, out page))
        {
            return false;
        }

        return true;
    }

    internal static bool TryReadRegionPageHeader(
        ReadOnlySpan<byte> source,
        long expectedTableIndex,
        int expectedCapacity,
        out int entryCount)
    {
        entryCount = 0;
        if (source.Length < RegionHeaderSize || !source[..8].SequenceEqual(RegionSignature))
        {
            return false;
        }

        long tableIndex = BinaryPrimitives.ReadInt64LittleEndian(source[16..]);
        uint serializedEntryCount = BinaryPrimitives.ReadUInt32LittleEndian(source[24..]);
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(source[28..]);
        if (tableIndex != expectedTableIndex || capacity != (uint)expectedCapacity || serializedEntryCount > (uint)expectedCapacity)
        {
            return false;
        }

        entryCount = (int)serializedEntryCount;
        return true;
    }

    internal static bool TryReadRegionPageParts(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> link,
        long expectedTableIndex,
        int expectedCapacity,
        out RegionPage? page)
    {
        page = null;
        if (!TryReadRegionPageHeader(prefix, expectedTableIndex, expectedCapacity, out int entryCount) ||
            prefix.Length != RegionHeaderSize + (entryCount * RegionEntrySize) ||
            link.Length != RegionEntrySize ||
            BinaryPrimitives.ReadUInt64LittleEndian(prefix[8..]) != ComputeRegionChecksum(prefix, link))
        {
            return false;
        }

        var entries = new List<RegionEntry>(entryCount);
        int cursor = RegionHeaderSize;
        for (int i = 0; i < entryCount; i++)
        {
            RegionEntry item = ReadRegionEntry(prefix[cursor..(cursor + RegionEntrySize)]);
            if (item.Kind is not (BatRegionKind or TrimRegionKind or DependentRegionKind) ||
                item.Flags != RequiredRegionFlag || item.LogicalIndex < 0 || item.PhysicalOffset <= 0)
            {
                return false;
            }

            entries.Add(item);
            cursor += RegionEntrySize;
        }

        RegionEntry linkEntry = ReadRegionEntry(link);
        long nextOffset;
        if (linkEntry == default)
        {
            nextOffset = 0;
        }
        else if (linkEntry.Kind == SubRegionKind && linkEntry.Flags == RequiredRegionFlag &&
            linkEntry.LogicalIndex == expectedTableIndex + 1 && linkEntry.PhysicalOffset > 0)
        {
            nextOffset = linkEntry.PhysicalOffset;
        }
        else
        {
            return false;
        }

        page = new RegionPage(expectedTableIndex, expectedCapacity, entries, nextOffset);
        return true;
    }

    private static ulong ComputeRegionChecksum(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> link)
    {
        var hasher = new XxHash64();
        Span<byte> header = stackalloc byte[RegionHeaderSize];
        prefix[..RegionHeaderSize].CopyTo(header);
        header.Slice(8, 8).Clear();
        hasher.Append(header);
        hasher.Append(prefix[RegionHeaderSize..]);
        hasher.Append(link);
        Span<byte> hash = stackalloc byte[8];
        hasher.GetCurrentHash(hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private static void WriteRegionEntry(Span<byte> destination, RegionEntry item)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, item.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], item.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], item.LogicalIndex);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], item.PhysicalOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], item.Reserved);
    }

    private static RegionEntry ReadRegionEntry(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(source),
        BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[24..]));

    internal static int GetDependentPageCapacity(int blockSize) =>
        (blockSize - DependentPageHeaderSize) / DependentSlotSize;

    internal static void WriteDependentPage(
        Span<byte> destination,
        long pageIndex,
        long nextOffset,
        IReadOnlyCollection<Guid> ids)
    {
        int capacity = GetDependentPageCapacity(destination.Length);
        if (ids.Count > capacity || pageIndex < 0 || nextOffset < 0)
        {
            throw new ArgumentException("Invalid dependent registry page.", nameof(ids));
        }

        destination.Clear();
        DependentSignature.CopyTo(destination);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], pageIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], (uint)ids.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], (uint)capacity);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], nextOffset);
        int cursor = DependentPageHeaderSize;
        foreach (Guid id in ids)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Dependent stream identifiers cannot be empty.", nameof(ids));
            }

            WriteGuid(destination[cursor..(cursor + DependentSlotSize)], id);
            cursor += DependentSlotSize;
        }

        WriteChecksum(destination);
    }

    internal static bool TryReadDependentPage(
        ReadOnlySpan<byte> source,
        long expectedPageIndex,
        out long nextOffset,
        out List<Guid> ids)
    {
        nextOffset = 0;
        ids = [];
        int capacity = GetDependentPageCapacity(source.Length);
        if (source.Length < MinimumBlockSize ||
            !source[..8].SequenceEqual(DependentSignature) ||
            !ValidateChecksum(source) ||
            BinaryPrimitives.ReadInt64LittleEndian(source[16..]) != expectedPageIndex ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[28..]) != (uint)capacity ||
            source[40..DependentPageHeaderSize].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        uint liveCount = BinaryPrimitives.ReadUInt32LittleEndian(source[24..]);
        nextOffset = BinaryPrimitives.ReadInt64LittleEndian(source[32..]);
        if (liveCount > (uint)capacity || nextOffset < 0)
        {
            return false;
        }

        var unique = new HashSet<Guid>();
        int cursor = DependentPageHeaderSize;
        for (int index = 0; index < capacity; index++)
        {
            Guid id = ReadGuid(source[cursor..(cursor + DependentSlotSize)]);
            cursor += DependentSlotSize;
            if (id != Guid.Empty && !unique.Add(id))
            {
                return false;
            }
        }

        if (unique.Count != liveCount || source[cursor..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        ids.AddRange(unique);
        return true;
    }

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

internal readonly record struct FormatIdentity(
    Guid Id,
    Guid InitialDataWriteId,
    ushort MajorVersion,
    ushort MinorVersion,
    int BlockSize,
    long VirtualCapacity);

internal sealed record RootState(
    ulong Generation,
    Guid Id,
    ushort MajorVersion,
    ushort MinorVersion,
    int BlockSize,
    long LogicalLength,
    long JournalOffset,
    int JournalLength,
    Guid ActiveLogId,
    int ActiveLogStartSlot,
    int ActiveLogEntryCount,
    ulong ActiveLogFirstSequence,
    int NextJournalSlot,
    long RequiredPhysicalLength,
    Guid DataWriteId,
    long VirtualCapacity)
{
    internal bool IsClean => ActiveLogId == Guid.Empty;
}

internal readonly record struct MetadataPatch(long Offset, long Value);

internal sealed record JournalEntry(
    ulong Sequence,
    Guid LogId,
    int EntryIndex,
    int EntryCount,
    long RequiredPhysicalLength,
    IReadOnlyList<MetadataPatch> Patches);

internal readonly record struct RegionEntry(
    uint Kind,
    uint Flags,
    long LogicalIndex,
    long PhysicalOffset,
    long Reserved = 0);

internal sealed class RegionPage
{
    internal RegionPage(long tableIndex, int capacity, List<RegionEntry> entries, long nextOffset)
    {
        TableIndex = tableIndex;
        Capacity = capacity;
        Entries = entries;
        NextOffset = nextOffset;
    }

    internal long TableIndex { get; }
    internal int Capacity { get; }
    internal List<RegionEntry> Entries { get; }
    internal long NextOffset { get; set; }
}
