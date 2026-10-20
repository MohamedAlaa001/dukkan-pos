namespace LocalCashier.Domain;

/// <summary>EAN-13 helpers for in-store barcodes (prefix 200 = GS1 "restricted circulation" range, safe for internal labels).</summary>
public static class Ean13
{
    // Left-hand digit encodings (7 modules): L (odd parity), G (even parity); right-hand R.
    static readonly string[] L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    static readonly string[] G = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
    static readonly string[] R = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];
    static readonly string[] Parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    public static char CheckDigit(string first12)
    {
        if (first12.Length != 12 || !first12.All(char.IsAsciiDigit)) throw new ArgumentException("EAN-13 needs 12 digits.", nameof(first12));
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    public static bool IsValid(string code) => code.Length == 13 && code.All(char.IsAsciiDigit) && CheckDigit(code[..12]) == code[12];

    /// <summary>Internal barcode for sequence number n (200 + 9-digit sequence + check digit).</summary>
    public static string FromSequence(long n)
    {
        if (n < 0 || n > 999_999_999) throw new ArgumentOutOfRangeException(nameof(n));
        var body = "200" + n.ToString("D9");
        return body + CheckDigit(body);
    }

    /// <summary>The 95 modules of the symbol as '1' (bar) / '0' (space), including guard patterns.</summary>
    public static string Modules(string code)
    {
        if (!IsValid(code)) throw new ArgumentException("Invalid EAN-13.", nameof(code));
        var parity = Parity[code[0] - '0'];
        var sb = new System.Text.StringBuilder("101");
        for (var i = 0; i < 6; i++)
        {
            var d = code[i + 1] - '0';
            sb.Append(parity[i] == 'L' ? L[d] : G[d]);
        }

        sb.Append("01010");
        for (var i = 7; i < 13; i++) sb.Append(R[code[i] - '0']);
        sb.Append("101");
        return sb.ToString();
    }
}
