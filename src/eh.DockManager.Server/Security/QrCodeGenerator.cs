// File: src/eh.DockManager.Server/Security/QrCodeGenerator.cs
// A self-contained QR Code Model 2 encoder (byte mode, ECC level M by default).
// Ported to C# from the public-domain / MIT "QR Code generator" reference algorithm by Project Nayuki.
// No third-party package dependency is required.
using System.Text;

namespace eh.DockManager.Server.Security;

/// <summary>Encodes text into a QR Code symbol and renders it as SVG or an ASCII matrix.</summary>
public static class QrCodeGenerator
{
    /// <summary>Renders the given text as a single SVG document. Ideal for TOTP enrollment.</summary>
    public static string ToSvg(string text, int scale = 6, int border = 2, EccLevel ecc = EccLevel.Medium)
    {
        var qr = QrSymbol.EncodeText(text, ecc);
        int size = qr.Size;
        int dim = size + border * 2;
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 ")
          .Append(dim).Append(' ').Append(dim)
          .Append("\" width=\"").Append(dim * scale).Append("\" height=\"").Append(dim * scale)
          .Append("\" shape-rendering=\"crispEdges\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>");
        sb.Append("<path fill=\"#000000\" d=\"");
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!qr.GetModule(x, y)) continue;
                sb.Append('M').Append(x + border).Append(',').Append(y + border).Append("h1v1h-1z");
            }
        }
        sb.Append("\"/></svg>");
        return sb.ToString();
    }

    /// <summary>Renders the symbol as a text matrix (double-width blocks) for terminal display.</summary>
    public static string ToAscii(string text, int border = 2, EccLevel ecc = EccLevel.Medium)
    {
        var qr = QrSymbol.EncodeText(text, ecc);
        int size = qr.Size;
        var sb = new StringBuilder();
        for (int y = -border; y < size + border; y++)
        {
            for (int x = -border; x < size + border; x++)
            {
                bool dark = x >= 0 && x < size && y >= 0 && y < size && qr.GetModule(x, y);
                sb.Append(dark ? "██" : "  ");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>QR error-correction levels ordered to match their internal ordinal.</summary>
public enum EccLevel
{
    Low = 0,
    Medium = 1,
    Quartile = 2,
    High = 3
}

/// <summary>A fully constructed QR Code Model 2 symbol.</summary>
internal sealed class QrSymbol
{
    private const int MinVersion = 1;
    private const int MaxVersion = 40;
    private const int PenaltyN1 = 3;
    private const int PenaltyN2 = 3;
    private const int PenaltyN3 = 40;
    private const int PenaltyN4 = 10;

    private static readonly int[][] EccCodeWordsPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 }
    };

    private static readonly int[][] NumErrorCorrectionBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 }
    };

    private readonly int _version;
    private readonly int _size;
    private readonly int _eccOrdinal;
    private readonly bool[] _modules;   // row-major, index = y * size + x
    private bool[] _isFunction = Array.Empty<bool>();

    private QrSymbol(int version, int eccOrdinal, byte[] dataCodeWords)
    {
        _version = version;
        _size = version * 4 + 17;
        _eccOrdinal = eccOrdinal;
        _modules = new bool[_size * _size];
    }

    // Convenience overload used by the constructors above.
    private static QrSymbol Build(int version, int eccOrdinal, byte[] dataCodeWords)
    {
        var qr = new QrSymbol(version, eccOrdinal, dataCodeWords);
        qr._isFunction = new bool[qr._size * qr._size];
        qr.DrawFunctionPatterns();
        var all = qr.AddEccAndInterleave(dataCodeWords);
        qr.DrawCodeWords(all);

        int bestMask = 0;
        int minPenalty = int.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            qr.ApplyMask(i);
            qr.DrawFormatBits(i);
            int penalty = qr.GetPenaltyScore();
            if (penalty < minPenalty) { bestMask = i; minPenalty = penalty; }
            qr.ApplyMask(i);
        }
        qr.ApplyMask(bestMask);
        qr.DrawFormatBits(bestMask);
        return qr;
    }

    public int Size => _size;

    public bool GetModule(int x, int y)
        => x >= 0 && x < _size && y >= 0 && y < _size && _modules[y * _size + x];

    public static QrSymbol EncodeText(string text, EccLevel ecc)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int eccOrdinal = (int)ecc;
        int version = MinVersion;
        while (version <= MaxVersion)
        {
            int capacityBits = GetNumDataCodeWords(version, eccOrdinal) * 8;
            int needed = 4 + CharCountBits(version) + bytes.Length * 8;
            if (needed <= capacityBits) break;
            version++;
        }
        if (version > MaxVersion) throw new ArgumentException("Data too long for a QR Code.", nameof(text));

        int dataCw = GetNumDataCodeWords(version, eccOrdinal);
        var bb = new BitBuffer();
        bb.AppendBits(0x4, 4);                       // Byte mode indicator
        bb.AppendBits(bytes.Length, CharCountBits(version));
        foreach (var b in bytes) bb.AppendBits(b, 8);

        int capacity = dataCw * 8;
        bb.AppendBits(0, Math.Min(4, capacity - bb.Length));     // Terminator
        bb.AppendBits(0, (8 - bb.Length % 8) % 8);               // Byte align
        for (int pad = 0xEC; bb.Length < capacity; pad ^= 0xEC ^ 0x11)
            bb.AppendBits(pad, 8);

        return Build(version, eccOrdinal, bb.ToBytes());
    }

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static int GetNumRawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int GetNumDataCodeWords(int ver, int eccOrdinal)
        => GetNumRawDataModules(ver) / 8
           - EccCodeWordsPerBlock[eccOrdinal][ver] * NumErrorCorrectionBlocks[eccOrdinal][ver];

    // ── Function patterns ─────────────────────────────────────────────────

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < _size; i++)
        {
            SetFunctionModule(6, i, i % 2 == 0);
            SetFunctionModule(i, 6, i % 2 == 0);
        }

        DrawFinderPattern(3, 3);
        DrawFinderPattern(_size - 4, 3);
        DrawFinderPattern(3, _size - 4);

        var alignPos = GetAlignmentPatternPositions();
        int n = alignPos.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                    DrawAlignmentPattern(alignPos[i], alignPos[j]);

        DrawFormatBits(0);
        DrawVersion();
    }

    private int[] GetAlignmentPatternPositions()
    {
        if (_version == 1) return Array.Empty<int>();
        int numAlign = _version / 7 + 2;
        int step = (_version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        for (int i = 0; i < numAlign - 1; i++) result[i] = _size - 7 - i * step;
        result[numAlign - 1] = 6;
        Array.Sort(result);
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        int[] formatBitsPerEcc = { 1, 0, 3, 2 };
        int data = formatBitsPerEcc[_eccOrdinal] << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunctionModule(8, i, GetBit(bits, i));
        SetFunctionModule(8, 7, GetBit(bits, 6));
        SetFunctionModule(8, 8, GetBit(bits, 7));
        SetFunctionModule(7, 8, GetBit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunctionModule(14 - i, 8, GetBit(bits, i));

        for (int i = 0; i < 8; i++) SetFunctionModule(_size - 1 - i, 8, GetBit(bits, i));
        for (int i = 8; i < 15; i++) SetFunctionModule(8, _size - 15 + i, GetBit(bits, i));
        SetFunctionModule(8, _size - 8, true);
    }

    private void DrawVersion()
    {
        if (_version < 7) return;
        int rem = _version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = _version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = GetBit(bits, i);
            int a = _size - 11 + i % 3;
            int b = i / 3;
            SetFunctionModule(a, b, bit);
            SetFunctionModule(b, a, bit);
        }
    }

    private void DrawFinderPattern(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < _size && yy >= 0 && yy < _size)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunctionModule(xx, yy, dist != 2 && dist != 4);
                }
            }
    }

    private void DrawAlignmentPattern(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunctionModule(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void SetFunctionModule(int x, int y, bool isDark)
    {
        _modules[y * _size + x] = isDark;
        _isFunction[y * _size + x] = true;
    }

    // ── Codewords ─────────────────────────────────────────────────────────

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int numBlocks = NumErrorCorrectionBlocks[_eccOrdinal][_version];
        int blockEccLen = EccCodeWordsPerBlock[_eccOrdinal][_version];
        int rawCodeWords = GetNumRawDataModules(_version) / 8;
        int numShortBlocks = numBlocks - rawCodeWords % numBlocks;
        int shortBlockLen = rawCodeWords / numBlocks;

        var blocks = new List<byte[]>(numBlocks);
        var rsDiv = ReedSolomonComputeDivisor(blockEccLen);
        int k = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;
            var ecc = ReedSolomonComputeRemainder(dat, rsDiv);
            var block = new byte[shortBlockLen + 1];
            Array.Copy(dat, block, datLen);
            if (i < numShortBlocks) { block[datLen] = 0; Array.Copy(ecc, 0, block, datLen + 1, ecc.Length); }
            else Array.Copy(ecc, 0, block, datLen, ecc.Length);
            blocks.Add(block);
        }

        var result = new byte[rawCodeWords];
        int idx = 0;
        for (int i = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[idx++] = blocks[j][i];
        return result;
    }

    private static byte[] ReedSolomonComputeDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = ReedSolomonMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = ReedSolomonMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < divisor.Length; i++)
                result[i] ^= ReedSolomonMultiply(divisor[i], factor);
        }
        return result;
    }

    private static byte ReedSolomonMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private void DrawCodeWords(byte[] data)
    {
        int i = 0;
        for (int right = _size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < _size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? _size - 1 - vert : vert;
                    if (!_isFunction[y * _size + x] && i < data.Length * 8)
                    {
                        _modules[y * _size + x] = GetBit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < _size; y++)
            for (int x = 0; x < _size; x++)
                if (!_isFunction[y * _size + x] && MaskBit(mask, x, y))
                    _modules[y * _size + x] = !_modules[y * _size + x];
    }

    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
        _ => false
    };

    // ── Penalty scoring ───────────────────────────────────────────────────

    private int GetPenaltyScore()
    {
        int result = 0;

        for (int y = 0; y < _size; y++)
        {
            bool runColor = false;
            int runX = 0;
            var history = new int[7];
            for (int x = 0; x < _size; x++)
            {
                if (Module(x, y) == runColor)
                {
                    runX++;
                    if (runX == 5) result += PenaltyN1;
                    else if (runX > 5) result++;
                }
                else
                {
                    FinderPenaltyAddHistory(history, runX);
                    if (!runColor) result += FinderPenaltyCountPatterns(history) * PenaltyN3;
                    runColor = Module(x, y);
                    runX = 1;
                }
            }
            result += FinderPenaltyTerminateAndCount(history, runColor, runX) * PenaltyN3;
        }

        for (int x = 0; x < _size; x++)
        {
            bool runColor = false;
            int runY = 0;
            var history = new int[7];
            for (int y = 0; y < _size; y++)
            {
                if (Module(x, y) == runColor)
                {
                    runY++;
                    if (runY == 5) result += PenaltyN1;
                    else if (runY > 5) result++;
                }
                else
                {
                    FinderPenaltyAddHistory(history, runY);
                    if (!runColor) result += FinderPenaltyCountPatterns(history) * PenaltyN3;
                    runColor = Module(x, y);
                    runY = 1;
                }
            }
            result += FinderPenaltyTerminateAndCount(history, runColor, runY) * PenaltyN3;
        }

        for (int y = 0; y < _size - 1; y++)
            for (int x = 0; x < _size - 1; x++)
            {
                bool c = Module(x, y);
                if (c == Module(x + 1, y) && c == Module(x, y + 1) && c == Module(x + 1, y + 1))
                    result += PenaltyN2;
            }

        int dark = 0;
        foreach (var m in _modules) if (m) dark++;
        int total = _size * _size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * PenaltyN4;
        return result;
    }

    private bool Module(int x, int y) => _modules[y * _size + x];

    private static int FinderPenaltyCountPatterns(int[] history)
    {
        int n = history[1];
        bool core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;
        return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0)
             + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
    }

    private int FinderPenaltyTerminateAndCount(int[] history, bool currentRunColor, int currentRunLength)
    {
        if (currentRunColor)
        {
            FinderPenaltyAddHistory(history, currentRunLength);
            currentRunLength = 0;
        }
        currentRunLength += _size;
        FinderPenaltyAddHistory(history, currentRunLength);
        return FinderPenaltyCountPatterns(history);
    }

    private void FinderPenaltyAddHistory(int[] history, int currentRunLength)
    {
        if (history[0] == 0) currentRunLength += _size;
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = currentRunLength;
    }

    private static bool GetBit(long x, int i) => ((x >> i) & 1) != 0;

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = new();

        public int Length => _bits.Count;

        public void AppendBits(int value, int len)
        {
            if (len < 0 || len > 31) throw new ArgumentOutOfRangeException(nameof(len));
            for (int i = len - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return bytes;
        }
    }
}
