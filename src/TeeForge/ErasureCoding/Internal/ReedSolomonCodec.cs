using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace TeeForge.ErasureCoding.Internal;

internal enum ReedSolomonAcceleration
{
    Auto,
    Scalar,
    Ssse3,
    Avx2,
}

internal sealed class ReedSolomonCodec : IReedSolomonCodec
{
    private static readonly byte[][] LowNibbleTables = CreateNibbleTables(high: false);
    private static readonly byte[][] HighNibbleTables = CreateNibbleTables(high: true);
    private readonly ReedSolomonAcceleration _acceleration;
    private readonly GaloisMatrix _encodingMatrix;

    internal ReedSolomonCodec(
        int dataShardCount,
        int parityShardCount,
        ReedSolomonAcceleration acceleration = ReedSolomonAcceleration.Auto)
    {
        _ = ErasureFormatV1.CalculateReadQuorum(dataShardCount, parityShardCount);

        if (!Enum.IsDefined(acceleration))
        {
            throw new ArgumentOutOfRangeException(nameof(acceleration));
        }

        if (acceleration == ReedSolomonAcceleration.Avx2 && !Avx2.IsSupported)
        {
            throw new PlatformNotSupportedException("AVX2 is not available.");
        }

        if (acceleration == ReedSolomonAcceleration.Ssse3 && !Ssse3.IsSupported)
        {
            throw new PlatformNotSupportedException("SSSE3 is not available.");
        }

        DataShardCount = dataShardCount;
        ParityShardCount = parityShardCount;
        _acceleration = acceleration;
        _encodingMatrix = CreateEncodingMatrix(dataShardCount, parityShardCount);
    }

    public int DataShardCount { get; }

    public int ParityShardCount { get; }

    internal bool IsHardwareAccelerated => _acceleration switch
    {
        ReedSolomonAcceleration.Auto => Avx2.IsSupported || Ssse3.IsSupported,
        ReedSolomonAcceleration.Scalar => false,
        _ => true,
    };

    public void Encode(byte[][] shards, int offset, int byteCount)
    {
        ValidateBuffers(shards, present: null, offset, byteCount);

        for (int parity = 0; parity < ParityShardCount; parity++)
        {
            CodeRow(
                _encodingMatrix.GetRowSpan(DataShardCount + parity),
                shards,
                DataShardCount,
                shards[DataShardCount + parity],
                offset,
                byteCount);
        }
    }

    public void Reconstruct(byte[][] shards, bool[] present, int offset, int byteCount)
    {
        ValidateBuffers(shards, present, offset, byteCount);

        int presentCount = 0;
        for (int index = 0; index < present.Length; index++)
        {
            if (present[index])
            {
                presentCount++;
            }
        }

        if (presentCount == shards.Length)
        {
            return;
        }

        if (presentCount < DataShardCount)
        {
            throw new InvalidDataException("Not enough shards are present to reconstruct the erasure set.");
        }

        var decodeRows = new GaloisMatrix(DataShardCount, DataShardCount);
        var decodeInputs = new byte[DataShardCount][];
        int inputIndex = 0;
        for (int member = 0; member < shards.Length && inputIndex < DataShardCount; member++)
        {
            if (!present[member])
            {
                continue;
            }

            decodeInputs[inputIndex] = shards[member];
            for (int column = 0; column < DataShardCount; column++)
            {
                decodeRows[inputIndex, column] = _encodingMatrix[member, column];
            }

            inputIndex++;
        }

        GaloisMatrix decodeMatrix = decodeRows.Invert();
        for (int data = 0; data < DataShardCount; data++)
        {
            if (present[data])
            {
                continue;
            }

            CodeRow(decodeMatrix.GetRowSpan(data), decodeInputs, DataShardCount, shards[data], offset, byteCount);
            present[data] = true;
        }

        for (int member = DataShardCount; member < shards.Length; member++)
        {
            if (present[member])
            {
                continue;
            }

            CodeRow(_encodingMatrix.GetRowSpan(member), shards, DataShardCount, shards[member], offset, byteCount);
            present[member] = true;
        }
    }

    private static GaloisMatrix CreateEncodingMatrix(int dataShardCount, int parityShardCount)
    {
        int memberCount = checked(dataShardCount + parityShardCount);
        GaloisMatrix vandermonde = GaloisMatrix.CreateVandermonde(memberCount, dataShardCount);
        GaloisMatrix top = vandermonde.GetSubmatrix(0, 0, dataShardCount, dataShardCount);
        return GaloisMatrix.Multiply(vandermonde, top.Invert());
    }

    private static byte[][] CreateNibbleTables(bool high)
    {
        var tables = new byte[256][];
        for (int coefficient = 0; coefficient < tables.Length; coefficient++)
        {
            var table = new byte[16];
            for (int nibble = 0; nibble < table.Length; nibble++)
            {
                int value = high ? nibble << 4 : nibble;
                table[nibble] = GaloisField256.Multiply((byte)coefficient, (byte)value);
            }

            tables[coefficient] = table;
        }

        return tables;
    }

    private void CodeRow(
        ReadOnlySpan<byte> coefficients,
        byte[][] inputs,
        int inputCount,
        byte[] output,
        int offset,
        int byteCount)
    {
        Span<byte> destination = output.AsSpan(offset, byteCount);
        destination.Clear();

        for (int input = 0; input < inputCount; input++)
        {
            byte coefficient = coefficients[input];
            if (coefficient == 0)
            {
                continue;
            }

            MultiplyXor(inputs[input].AsSpan(offset, byteCount), destination, coefficient);
        }
    }

    private void MultiplyXor(ReadOnlySpan<byte> input, Span<byte> output, byte coefficient)
    {
        int index = 0;
        if (_acceleration == ReedSolomonAcceleration.Avx2 ||
            (_acceleration == ReedSolomonAcceleration.Auto && Avx2.IsSupported))
        {
            index = MultiplyXorAvx2(input, output, coefficient);
        }
        else if (_acceleration == ReedSolomonAcceleration.Ssse3 ||
            (_acceleration == ReedSolomonAcceleration.Auto && Ssse3.IsSupported))
        {
            index = MultiplyXorSsse3(input, output, coefficient);
        }

        for (; index < input.Length; index++)
        {
            output[index] ^= coefficient == 1
                ? input[index]
                : GaloisField256.Multiply(coefficient, input[index]);
        }
    }

    private static int MultiplyXorAvx2(ReadOnlySpan<byte> input, Span<byte> output, byte coefficient)
    {
        ref byte inputReference = ref MemoryMarshal.GetReference(input);
        ref byte outputReference = ref MemoryMarshal.GetReference(output);
        Vector256<byte> nibbleMask = Vector256.Create((byte)0x0F);
        Vector128<byte> low128 = Vector128.LoadUnsafe(ref LowNibbleTables[coefficient][0]);
        Vector128<byte> high128 = Vector128.LoadUnsafe(ref HighNibbleTables[coefficient][0]);
        Vector256<byte> lowLookup = Vector256.Create(low128, low128);
        Vector256<byte> highLookup = Vector256.Create(high128, high128);
        int index = 0;
        int vectorEnd = input.Length - Vector256<byte>.Count;

        for (; index <= vectorEnd; index += Vector256<byte>.Count)
        {
            Vector256<byte> source = Vector256.LoadUnsafe(ref inputReference, (nuint)index);
            Vector256<byte> lowNibbles = Avx2.And(source, nibbleMask);
            Vector256<byte> highNibbles = Avx2.And(
                Avx2.ShiftRightLogical(source.AsUInt16(), 4).AsByte(),
                nibbleMask);
            Vector256<byte> product = Avx2.Xor(
                Avx2.Shuffle(lowLookup, lowNibbles),
                Avx2.Shuffle(highLookup, highNibbles));
            Vector256<byte> current = Vector256.LoadUnsafe(ref outputReference, (nuint)index);
            Avx2.Xor(current, product).StoreUnsafe(ref outputReference, (nuint)index);
        }

        return index;
    }

    private static int MultiplyXorSsse3(ReadOnlySpan<byte> input, Span<byte> output, byte coefficient)
    {
        ref byte inputReference = ref MemoryMarshal.GetReference(input);
        ref byte outputReference = ref MemoryMarshal.GetReference(output);
        Vector128<byte> nibbleMask = Vector128.Create((byte)0x0F);
        Vector128<byte> lowLookup = Vector128.LoadUnsafe(ref LowNibbleTables[coefficient][0]);
        Vector128<byte> highLookup = Vector128.LoadUnsafe(ref HighNibbleTables[coefficient][0]);
        int index = 0;
        int vectorEnd = input.Length - Vector128<byte>.Count;

        for (; index <= vectorEnd; index += Vector128<byte>.Count)
        {
            Vector128<byte> source = Vector128.LoadUnsafe(ref inputReference, (nuint)index);
            Vector128<byte> lowNibbles = Sse2.And(source, nibbleMask);
            Vector128<byte> highNibbles = Sse2.And(
                Sse2.ShiftRightLogical(source.AsUInt16(), 4).AsByte(),
                nibbleMask);
            Vector128<byte> product = Sse2.Xor(
                Ssse3.Shuffle(lowLookup, lowNibbles),
                Ssse3.Shuffle(highLookup, highNibbles));
            Vector128<byte> current = Vector128.LoadUnsafe(ref outputReference, (nuint)index);
            Sse2.Xor(current, product).StoreUnsafe(ref outputReference, (nuint)index);
        }

        return index;
    }

    private void ValidateBuffers(byte[][] shards, bool[]? present, int offset, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(shards);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        int memberCount = DataShardCount + ParityShardCount;
        if (shards.Length != memberCount)
        {
            throw new ArgumentException($"Exactly {memberCount} shard buffers are required.", nameof(shards));
        }

        if (present is not null && present.Length != memberCount)
        {
            throw new ArgumentException($"Exactly {memberCount} presence values are required.", nameof(present));
        }

        for (int index = 0; index < shards.Length; index++)
        {
            byte[] shard = shards[index] ?? throw new ArgumentException("Shard buffers cannot contain null.", nameof(shards));
            if (offset > shard.Length - byteCount)
            {
                throw new ArgumentException("The requested range must fit every shard buffer.", nameof(byteCount));
            }
        }
    }
}
