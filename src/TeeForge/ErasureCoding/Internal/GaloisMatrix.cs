namespace TeeForge.ErasureCoding.Internal;

internal sealed class GaloisMatrix
{
    private readonly byte[] _values;

    internal GaloisMatrix(int rowCount, int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columnCount);

        RowCount = rowCount;
        ColumnCount = columnCount;
        _values = new byte[checked(rowCount * columnCount)];
    }

    internal int RowCount { get; }

    internal int ColumnCount { get; }

    internal byte this[int row, int column]
    {
        get => _values[GetIndex(row, column)];
        set => _values[GetIndex(row, column)] = value;
    }

    internal static GaloisMatrix CreateVandermonde(int rowCount, int columnCount)
    {
        var result = new GaloisMatrix(rowCount, columnCount);
        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                result[row, column] = GaloisField256.Power((byte)row, column);
            }
        }

        return result;
    }

    internal static GaloisMatrix Multiply(GaloisMatrix left, GaloisMatrix right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.ColumnCount != right.RowCount)
        {
            throw new ArgumentException("Matrix dimensions are incompatible.", nameof(right));
        }

        var result = new GaloisMatrix(left.RowCount, right.ColumnCount);
        for (int row = 0; row < result.RowCount; row++)
        {
            for (int column = 0; column < result.ColumnCount; column++)
            {
                byte value = 0;
                for (int index = 0; index < left.ColumnCount; index++)
                {
                    value ^= GaloisField256.Multiply(left[row, index], right[index, column]);
                }

                result[row, column] = value;
            }
        }

        return result;
    }

    internal GaloisMatrix GetSubmatrix(int row, int column, int rowCount, int columnCount)
    {
        if (row < 0 || column < 0 || rowCount <= 0 || columnCount <= 0 ||
            row > RowCount - rowCount || column > ColumnCount - columnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        var result = new GaloisMatrix(rowCount, columnCount);
        for (int targetRow = 0; targetRow < rowCount; targetRow++)
        {
            for (int targetColumn = 0; targetColumn < columnCount; targetColumn++)
            {
                result[targetRow, targetColumn] = this[row + targetRow, column + targetColumn];
            }
        }

        return result;
    }

    internal GaloisMatrix Invert()
    {
        if (RowCount != ColumnCount)
        {
            throw new InvalidOperationException("Only square matrices can be inverted.");
        }

        int size = RowCount;
        var work = new GaloisMatrix(size, checked(size * 2));
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                work[row, column] = this[row, column];
            }

            work[row, size + row] = 1;
        }

        for (int pivot = 0; pivot < size; pivot++)
        {
            if (work[pivot, pivot] == 0)
            {
                int replacement = pivot + 1;
                while (replacement < size && work[replacement, pivot] == 0)
                {
                    replacement++;
                }

                if (replacement == size)
                {
                    throw new InvalidOperationException("Matrix is singular.");
                }

                work.SwapRows(pivot, replacement);
            }

            byte pivotValue = work[pivot, pivot];
            if (pivotValue != 1)
            {
                byte scale = GaloisField256.Divide(1, pivotValue);
                work.MultiplyRow(pivot, scale);
            }

            for (int row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                byte scale = work[row, pivot];
                if (scale != 0)
                {
                    work.AddScaledRow(row, pivot, scale);
                }
            }
        }

        return work.GetSubmatrix(0, size, size, size);
    }

    internal ReadOnlySpan<byte> GetRowSpan(int row)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)row, (uint)RowCount);
        return _values.AsSpan(row * ColumnCount, ColumnCount);
    }

    private int GetIndex(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)row, (uint)RowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)column, (uint)ColumnCount);
        return (row * ColumnCount) + column;
    }

    private void SwapRows(int first, int second)
    {
        for (int column = 0; column < ColumnCount; column++)
        {
            (this[first, column], this[second, column]) = (this[second, column], this[first, column]);
        }
    }

    private void MultiplyRow(int row, byte scale)
    {
        for (int column = 0; column < ColumnCount; column++)
        {
            this[row, column] = GaloisField256.Multiply(this[row, column], scale);
        }
    }

    private void AddScaledRow(int targetRow, int sourceRow, byte scale)
    {
        for (int column = 0; column < ColumnCount; column++)
        {
            this[targetRow, column] ^= GaloisField256.Multiply(scale, this[sourceRow, column]);
        }
    }
}
