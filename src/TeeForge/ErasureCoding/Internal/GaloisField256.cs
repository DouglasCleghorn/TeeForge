namespace TeeForge.ErasureCoding.Internal;

internal static class GaloisField256
{
    private const int FieldSize = 256;
    private const int MultiplicativeOrder = FieldSize - 1;
    private const int PrimitivePolynomial = 0x11D;
    private static readonly byte[] Exponents = CreateExponents();
    private static readonly byte[] Logarithms = CreateLogarithms();

    internal static byte Add(byte left, byte right) => (byte)(left ^ right);

    internal static byte Multiply(byte left, byte right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        return Exponents[Logarithms[left] + Logarithms[right]];
    }

    internal static byte Divide(byte dividend, byte divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException();
        }

        if (dividend == 0)
        {
            return 0;
        }

        int difference = Logarithms[dividend] - Logarithms[divisor];
        return Exponents[difference < 0 ? difference + MultiplicativeOrder : difference];
    }

    internal static byte Power(byte value, int exponent)
    {
        if (exponent == 0)
        {
            return 1;
        }

        if (value == 0)
        {
            return 0;
        }

        return Exponents[(Logarithms[value] * exponent) % MultiplicativeOrder];
    }

    private static byte[] CreateExponents()
    {
        var result = new byte[MultiplicativeOrder * 2];
        int value = 1;

        for (int index = 0; index < MultiplicativeOrder; index++)
        {
            result[index] = (byte)value;
            value <<= 1;
            if ((value & FieldSize) != 0)
            {
                value ^= PrimitivePolynomial;
            }
        }

        for (int index = MultiplicativeOrder; index < result.Length; index++)
        {
            result[index] = result[index - MultiplicativeOrder];
        }

        return result;
    }

    private static byte[] CreateLogarithms()
    {
        var result = new byte[FieldSize];
        for (int exponent = 0; exponent < MultiplicativeOrder; exponent++)
        {
            result[Exponents[exponent]] = (byte)exponent;
        }

        return result;
    }
}
