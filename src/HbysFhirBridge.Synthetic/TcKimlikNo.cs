namespace HbysFhirBridge.Synthetic;

/// <summary>
/// The TC Kimlik No (Turkish national identity number) checksum: 11 digits, first digit non-zero,
/// d10 = ((d1+d3+d5+d7+d9)·7 − (d2+d4+d6+d8)) mod 10 and d11 = (d1+…+d10) mod 10. The generator draws the first nine
/// digits at random, so its output is checksum-valid but synthetic; it is never derived from a real person.
/// </summary>
public static class TcKimlikNo
{
    public static string Generate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        Span<int> d = stackalloc int[11];
        d[0] = random.Next(1, 10);
        for (var i = 1; i < 9; i++)
        {
            d[i] = random.Next(0, 10);
        }

        d[9] = CheckDigit10(d);
        d[10] = CheckDigit11(d);

        Span<char> chars = stackalloc char[11];
        for (var i = 0; i < 11; i++)
        {
            chars[i] = (char)('0' + d[i]);
        }

        return new string(chars);
    }

    public static bool IsValid(string? value)
    {
        if (value is not { Length: 11 } || value[0] == '0' || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        Span<int> d = stackalloc int[11];
        for (var i = 0; i < 11; i++)
        {
            d[i] = value[i] - '0';
        }

        return d[9] == CheckDigit10(d) && d[10] == CheckDigit11(d);
    }

    private static int CheckDigit10(ReadOnlySpan<int> d)
    {
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        // ((odd * 7) - even) can be negative; normalise into 0..9.
        return (((odd * 7) - even) % 10 + 10) % 10;
    }

    private static int CheckDigit11(ReadOnlySpan<int> d)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += d[i];
        }

        return sum % 10;
    }
}
