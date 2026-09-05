using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal static class ErasureImageSuperblockSerializer
{
    internal const int PageSize = 4096;
    internal const int DirectoryOffset = 256;
    internal const int MaximumMemberCount = (PageSize - DirectoryOffset) / 16;
    internal const ushort MajorVersion = 1;
    internal const ushort MinorVersion = 0;
    internal const uint CodecId = 1;
    internal const uint LayoutId = 1;

    private const int HashOffset = 160;
    private const int HashLength = 16;
    private static ReadOnlySpan<byte> Magic => "TeeERS\r\n"u8;

    internal static void Write(ErasureImageHeader header, Span<byte> destination)
    {
        Validate(header);
        if (destination.Length < PageSize)
        {
            throw new ArgumentException("A complete 4096-byte superblock destination is required.", nameof(destination));
        }

        Span<byte> page = destination[..PageSize];
        page.Clear();
        Magic.CopyTo(page);
        BinaryPrimitives.WriteUInt16LittleEndian(page[8..], header.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(page[10..], header.MinorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(page[12..], 176);
        BinaryPrimitives.WriteUInt16LittleEndian(page[14..], PageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(page[16..], header.RequiredFeatures);
        BinaryPrimitives.WriteUInt64LittleEndian(page[24..], header.CompatibleFeatures);
        WriteGuid(page[32..48], header.SetId);
        WriteGuid(page[48..64], header.ConfigurationId);
        BinaryPrimitives.WriteUInt64LittleEndian(page[64..], header.ConfigurationGeneration);
        WriteGuid(page[72..88], header.MemberId);
        BinaryPrimitives.WriteUInt16LittleEndian(page[88..], header.MemberPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(page[90..], header.DataShardCount);
        BinaryPrimitives.WriteUInt16LittleEndian(page[92..], header.ParityShardCount);
        BinaryPrimitives.WriteUInt16LittleEndian(page[94..], checked((ushort)header.MemberCount));
        BinaryPrimitives.WriteUInt16LittleEndian(page[96..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(page[98..], DirectoryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(page[100..], header.CodecId);
        BinaryPrimitives.WriteUInt32LittleEndian(page[104..], header.LayoutId);
        BinaryPrimitives.WriteUInt32LittleEndian(page[108..], header.BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(page[112..], header.MemberRecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(page[120..], header.DataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(page[128..], header.LogicalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(page[136..], header.DataAlignment);

        for (int index = 0; index < header.MemberIds.Count; index++)
        {
            WriteGuid(page.Slice(DirectoryOffset + index * 16, 16), header.MemberIds[index]);
        }

        UInt128 hash = XxHash128.HashToUInt128(page);
        BinaryPrimitives.WriteUInt128LittleEndian(page[HashOffset..], hash);
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, out ErasureImageHeader? header)
    {
        header = null;
        if (source.Length < PageSize)
        {
            return false;
        }

        ReadOnlySpan<byte> page = source[..PageSize];
        if (!page[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[8..]) != MajorVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[12..]) != 176 ||
            BinaryPrimitives.ReadUInt16LittleEndian(page[14..]) != PageSize)
        {
            return false;
        }

        UInt128 expected = BinaryPrimitives.ReadUInt128LittleEndian(page[HashOffset..]);
        Span<byte> hashInput = stackalloc byte[PageSize];
        page.CopyTo(hashInput);
        hashInput.Slice(HashOffset, HashLength).Clear();
        if (expected != XxHash128.HashToUInt128(hashInput))
        {
            return false;
        }

        ushort memberCount = BinaryPrimitives.ReadUInt16LittleEndian(page[94..]);
        ushort entrySize = BinaryPrimitives.ReadUInt16LittleEndian(page[96..]);
        ushort directoryOffset = BinaryPrimitives.ReadUInt16LittleEndian(page[98..]);
        if (memberCount == 0 || memberCount > MaximumMemberCount || entrySize != 16 ||
            directoryOffset != DirectoryOffset)
        {
            return false;
        }

        var ids = new Guid[memberCount];
        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] = ReadGuid(page.Slice(DirectoryOffset + index * 16, 16));
        }

        var candidate = new ErasureImageHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(page[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[24..]),
            ReadGuid(page[32..48]),
            ReadGuid(page[48..64]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[64..]),
            ReadGuid(page[72..88]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[88..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[90..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[92..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[100..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[104..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[108..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[112..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[120..]),
            BinaryPrimitives.ReadUInt64LittleEndian(page[128..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[136..]),
            Array.AsReadOnly(ids));

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

        header = candidate;
        return true;
    }

    private static void Validate(ErasureImageHeader header)
    {
        if (header.MajorVersion != MajorVersion || header.RequiredFeatures != 0 || header.SetId == Guid.Empty ||
            header.ConfigurationId == Guid.Empty || header.MemberId == Guid.Empty)
        {
            throw new ArgumentException("The superblock identity is invalid.", nameof(header));
        }

        if (header.DataShardCount < 2 || header.ParityShardCount < 1 ||
            header.MemberCount != header.DataShardCount + header.ParityShardCount ||
            header.MemberCount > MaximumMemberCount || header.MemberPosition >= header.MemberCount ||
            header.MemberIds[header.MemberPosition] != header.MemberId ||
            header.MemberIds.Any(static id => id == Guid.Empty) ||
            header.MemberIds.Distinct().Count() != header.MemberCount)
        {
            throw new ArgumentException("The superblock member directory is invalid.", nameof(header));
        }

        if (header.CodecId != CodecId || header.LayoutId != LayoutId ||
            header.BlockSize < 4096 || header.BlockSize > 1024 * 1024 || !int.IsPow2((int)header.BlockSize) ||
            header.MemberRecordSize != header.BlockSize || header.DataAlignment < 4096 ||
            !int.IsPow2((int)header.DataAlignment) || header.DataOffset < 2 * PageSize ||
            header.DataOffset % header.DataAlignment != 0)
        {
            throw new ArgumentException("The superblock geometry is invalid.", nameof(header));
        }
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("A UUID must occupy exactly 16 bytes.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}
