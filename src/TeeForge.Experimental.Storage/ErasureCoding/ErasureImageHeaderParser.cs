using TeeForge.Experimental.Storage.ErasureCoding.Internal;

namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Inspects optional self-describing <see cref="ErasureImage"/> member superblocks.</summary>
public static class ErasureImageHeaderParser
{
    /// <summary>Parses one 4096-byte superblock page.</summary>
    public static ErasureImageHeader Parse(ReadOnlySpan<byte> superblock)
    {
        return TryParse(superblock, out ErasureImageHeader? header)
            ? header!
            : throw new InvalidDataException("The bytes do not contain a valid ErasureStream superblock.");
    }

    /// <summary>Attempts to parse one 4096-byte superblock page.</summary>
    public static bool TryParse(ReadOnlySpan<byte> superblock, out ErasureImageHeader? header) =>
        ErasureImageSuperblockSerializer.TryRead(superblock, out header);

    /// <summary>Reads and validates a member header while preserving position for seekable streams.</summary>
    public static ErasureImageHeader Read(Stream member)
    {
        return TryRead(member, out ErasureImageHeader? header)
            ? header!
            : throw new InvalidDataException("The stream does not contain a valid ErasureStream superblock.");
    }

    /// <summary>Attempts to read a member header while preserving position for seekable streams.</summary>
    public static bool TryRead(Stream member, out ErasureImageHeader? header)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (!member.CanRead)
        {
            header = null;
            return false;
        }

        long original = member.CanSeek ? member.Position : 0;
        try
        {
            if (member.CanSeek)
            {
                member.Position = 0;
            }

            byte[] primary = new byte[ErasureImageSuperblockSerializer.PageSize];
            if (!TryReadExactly(member, primary))
            {
                header = null;
                return false;
            }

            if (!member.CanSeek)
            {
                if (!TryParse(primary, out ErasureImageHeader? forwardHeader))
                {
                    header = null;
                    return false;
                }

                header = forwardHeader;
                return true;
            }

            bool hasFirst = TryParse(primary, out ErasureImageHeader? first);
            member.Position = ErasureImageSuperblockSerializer.PageSize;
            byte[] secondary = new byte[ErasureImageSuperblockSerializer.PageSize];
            ErasureImageHeader? second = null;
            bool hasSecond = TryReadExactly(member, secondary) &&
                TryParse(secondary, out second);

            if (!hasFirst && !hasSecond)
            {
                header = null;
                return false;
            }

            if (hasFirst && hasSecond && !Equivalent(first!, second!))
            {
                header = null;
                return false;
            }

            if (hasFirst)
            {
                header = first;
                return true;
            }

            header = second;
            return true;
        }
        finally
        {
            if (member.CanSeek)
            {
                member.Position = original;
            }
        }
    }

    /// <summary>Asynchronously reads and validates a member header while preserving seekable position.</summary>
    public static async ValueTask<ErasureImageHeader> ReadAsync(
        Stream member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        cancellationToken.ThrowIfCancellationRequested();
        if (!member.CanRead)
        {
            throw new NotSupportedException("The member stream is not readable.");
        }

        long original = member.CanSeek ? member.Position : 0;
        try
        {
            if (member.CanSeek)
            {
                member.Position = 0;
            }

            byte[] primary = new byte[ErasureImageSuperblockSerializer.PageSize];
            await member.ReadExactlyAsync(primary, cancellationToken).ConfigureAwait(false);
            if (!member.CanSeek)
            {
                if (!TryParse(primary, out ErasureImageHeader? forwardHeader))
                {
                    throw new InvalidDataException("The stream does not contain a valid ErasureStream superblock.");
                }

                return forwardHeader!;
            }

            bool hasFirst = TryParse(primary, out ErasureImageHeader? first);
            member.Position = ErasureImageSuperblockSerializer.PageSize;
            byte[] secondary = new byte[ErasureImageSuperblockSerializer.PageSize];
            bool readSecond = await TryReadExactlyAsync(member, secondary, cancellationToken).ConfigureAwait(false);
            ErasureImageHeader? second = null;
            bool hasSecond = readSecond &&
                TryParse(secondary, out second);
            if (!hasFirst && !hasSecond)
            {
                throw new InvalidDataException("The stream does not contain a valid ErasureStream superblock.");
            }

            if (hasFirst && hasSecond && !Equivalent(first!, second!))
            {
                throw new InvalidDataException("The member's duplicate superblocks conflict.");
            }

            return (hasFirst ? first : second)!;
        }
        finally
        {
            if (member.CanSeek)
            {
                member.Position = original;
            }
        }
    }

    internal static bool Equivalent(ErasureImageHeader left, ErasureImageHeader right) =>
        left.SetId == right.SetId && left.ConfigurationId == right.ConfigurationId &&
        left.ConfigurationGeneration == right.ConfigurationGeneration && left.MemberId == right.MemberId &&
        left.MemberPosition == right.MemberPosition && left.DataShardCount == right.DataShardCount &&
        left.ParityShardCount == right.ParityShardCount && left.BlockSize == right.BlockSize &&
        left.MemberRecordSize == right.MemberRecordSize && left.DataOffset == right.DataOffset &&
        left.LogicalLength == right.LogicalLength && left.MemberIds.SequenceEqual(right.MemberIds);

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = stream.Read(destination[completed..]);
            if (read == 0)
            {
                return false;
            }

            completed += read;
        }

        return true;
    }

    private static async ValueTask<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = await stream.ReadAsync(destination[completed..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            completed += read;
        }

        return true;
    }
}
