using System.Runtime.Intrinsics.X86;
using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ReedSolomonCodecTests
{
    [Fact]
    public void Three_plus_two_encoding_matches_the_version_one_golden_vector()
    {
        var codec = new ReedSolomonCodec(3, 2, ReedSolomonAcceleration.Scalar);
        byte[][] shards =
        [
            Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"),
            Convert.FromHexString("FFEEDDCCBBAA99887766554433221100"),
            new byte[16],
            new byte[16],
        ];

        codec.Encode(shards, 0, 16);

        Assert.Equal("EFCEED8CEBCAE908E7C6E584E3C2E100", Convert.ToHexString(shards[3]));
        Assert.Equal("B84467A11BE7C476E31F3CFA40BC9F2D", Convert.ToHexString(shards[4]));
    }

    [Fact]
    public void Encoding_is_systematic_and_reconstructs_every_supported_two_member_loss()
    {
        const int dataCount = 6;
        const int parityCount = 2;
        const int shardSize = 64 * 1024;
        var codec = new ReedSolomonCodec(dataCount, parityCount, ReedSolomonAcceleration.Scalar);
        byte[][] shards = CreateShards(dataCount + parityCount, shardSize, dataCount);
        byte[][] originalData = shards.Take(dataCount).Select(static shard => shard.ToArray()).ToArray();

        codec.Encode(shards, 0, shardSize);

        for (int data = 0; data < dataCount; data++)
        {
            Assert.Equal(originalData[data], shards[data]);
        }

        byte[][] encoded = shards.Select(static shard => shard.ToArray()).ToArray();
        for (int first = 0; first < shards.Length; first++)
        {
            VerifyReconstruction(codec, encoded, first);
            for (int second = first + 1; second < shards.Length; second++)
            {
                VerifyReconstruction(codec, encoded, first, second);
            }
        }
    }

    [Fact]
    public void Auto_acceleration_matches_scalar_for_unaligned_ranges_and_tail_bytes()
    {
        const int dataCount = 6;
        const int parityCount = 4;
        const int offset = 7;
        const int byteCount = (64 * 1024) + 13;
        int bufferSize = offset + byteCount + 11;
        byte[][] scalarShards = CreateShards(dataCount + parityCount, bufferSize, dataCount);
        byte[][] acceleratedShards = scalarShards.Select(static shard => shard.ToArray()).ToArray();
        var scalar = new ReedSolomonCodec(dataCount, parityCount, ReedSolomonAcceleration.Scalar);
        var accelerated = new ReedSolomonCodec(dataCount, parityCount);

        scalar.Encode(scalarShards, offset, byteCount);
        accelerated.Encode(acceleratedShards, offset, byteCount);

        for (int member = 0; member < scalarShards.Length; member++)
        {
            Assert.Equal(scalarShards[member], acceleratedShards[member]);
        }
    }

    [Fact]
    public void Ssse3_acceleration_matches_scalar_when_supported()
    {
        if (!Ssse3.IsSupported)
        {
            return;
        }

        const int dataCount = 6;
        const int parityCount = 2;
        const int shardSize = (64 * 1024) + 13;
        byte[][] scalarShards = CreateShards(dataCount + parityCount, shardSize, dataCount);
        byte[][] acceleratedShards = scalarShards.Select(static shard => shard.ToArray()).ToArray();
        var scalar = new ReedSolomonCodec(dataCount, parityCount, ReedSolomonAcceleration.Scalar);
        var accelerated = new ReedSolomonCodec(dataCount, parityCount, ReedSolomonAcceleration.Ssse3);

        scalar.Encode(scalarShards, 0, shardSize);
        accelerated.Encode(acceleratedShards, 0, shardSize);

        for (int member = 0; member < scalarShards.Length; member++)
        {
            Assert.Equal(scalarShards[member], acceleratedShards[member]);
        }
    }

    [Fact]
    public void Reconstruction_changes_only_the_requested_range()
    {
        const int dataCount = 4;
        const int parityCount = 2;
        const int bufferSize = 4096;
        const int offset = 31;
        const int byteCount = 3001;
        var codec = new ReedSolomonCodec(dataCount, parityCount);
        byte[][] shards = CreateShards(dataCount + parityCount, bufferSize, dataCount);
        codec.Encode(shards, offset, byteCount);
        byte[][] encoded = shards.Select(static shard => shard.ToArray()).ToArray();
        bool[] present = Enumerable.Repeat(true, shards.Length).ToArray();
        present[1] = false;
        shards[1].AsSpan(offset, byteCount).Clear();

        codec.Reconstruct(shards, present, offset, byteCount);

        Assert.Equal(encoded[1], shards[1]);
    }

    [Fact]
    public void Reconstruction_rejects_fewer_than_data_shards()
    {
        var codec = new ReedSolomonCodec(4, 2);
        byte[][] shards = CreateShards(6, 1024, initializedCount: 4);
        bool[] present = [true, true, true, false, false, false];

        Assert.Throws<InvalidDataException>(() => codec.Reconstruct(shards, present, 0, 1024));
    }

    [Fact]
    public void Matrix_inverse_round_trips_vandermonde_top()
    {
        for (int size = 2; size <= 32; size++)
        {
            GaloisMatrix matrix = GaloisMatrix.CreateVandermonde(size, size);
            GaloisMatrix product = GaloisMatrix.Multiply(matrix, matrix.Invert());

            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    Assert.Equal(row == column ? (byte)1 : (byte)0, product[row, column]);
                }
            }
        }
    }

    private static byte[][] CreateShards(int memberCount, int shardSize, int initializedCount)
    {
        var random = new Random(0x5EED);
        var shards = new byte[memberCount][];
        for (int member = 0; member < memberCount; member++)
        {
            shards[member] = new byte[shardSize];
            if (member < initializedCount)
            {
                random.NextBytes(shards[member]);
            }
            else
            {
                shards[member].AsSpan().Fill(0xA5);
            }
        }

        return shards;
    }

    private static void VerifyReconstruction(ReedSolomonCodec codec, byte[][] encoded, params int[] missing)
    {
        byte[][] candidate = encoded.Select(static shard => shard.ToArray()).ToArray();
        bool[] present = Enumerable.Repeat(true, candidate.Length).ToArray();
        foreach (int member in missing)
        {
            candidate[member].AsSpan().Clear();
            present[member] = false;
        }

        codec.Reconstruct(candidate, present, 0, candidate[0].Length);

        Assert.All(present, Assert.True);
        for (int member = 0; member < candidate.Length; member++)
        {
            Assert.Equal(encoded[member], candidate[member]);
        }
    }
}
