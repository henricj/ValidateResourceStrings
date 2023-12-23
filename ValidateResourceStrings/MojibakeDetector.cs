using System.Text;

namespace ValidateResourceStrings;

// Cribbed from https://devblogs.microsoft.com/oldnewthing/20190701-00/?p=102636

sealed class MojibakeDetector
{
    static readonly Dictionary<char, byte> To1252Table = new(Init1252Table());

    int _invalidFiles;

    public int InvalidFiles => _invalidFiles;

    public void ResourceValidator(string filename, ReadOnlySpan<char> buffer)
    {
        for (var i = 0; i < 16 && !buffer.IsEmpty; ++i)
        {
            var size = buffer[0];
            if (size > 0)
                ValidateString(filename, buffer.Slice(1, size));

            buffer = buffer[(size + 1)..];
        }
    }

    static IEnumerable<KeyValuePair<char, byte>> Init1252Table()
    {
        var as8bit = new byte[32];

        for (var i = 0; i < as8bit.Length; ++i)
            as8bit[i] = unchecked((byte)(i + 0x80));

        var encoding = Encoding.GetEncoding(1252);

        var encoded = encoding.GetChars(as8bit);

        for (var i = 0; i < encoded.Length; ++i)
        {
            var code = unchecked((byte)(i + 0x80));
            yield return new(encoded[i], code);
        }
    }

    byte To1252(char ch)
    {
        if (ch < 0x100)
            return (byte)ch;

        if (To1252Table.TryGetValue(ch, out var code))
            return code;

        return 0;
    }

    void ValidateString(string filename, ReadOnlySpan<char> p)
    {
        var j = 0;
        for (; j < p.Length; ++j)
        {
            if (p[j] == 0xFFFD)
            {
                // Ugh, REPLACEMENT CHARACTER.
                // Original *.rc was corrupted.
                break;
            }

            var ch = To1252(p[j]);
            if (ch > 0x7f)
            {
                // Does this look like UTF-8?
                if ((ch & 0xE0) == 0xC0
                    && j + 1 < p.Length && (To1252(p[j + 1]) & 0xC0) == 0x80)
                {
                    break;
                }

                if ((ch & 0xf0) == 0xE0
                    && j + 1 < p.Length && (To1252(p[j + 1]) & 0xC0) == 0x80
                    && j + 2 < p.Length && (To1252(p[j + 2]) & 0xC0) == 0x80)
                {
                    break;
                }

                if ((ch & 0xf8) == 0xF0
                    && j + 1 < p.Length && (To1252(p[j + 1]) & 0xC0) == 0x80
                    && j + 2 < p.Length && (To1252(p[j + 2]) & 0xC0) == 0x80
                    && j + 3 < p.Length && (To1252(p[j + 3]) & 0xC0) == 0x80)
                {
                    break;
                }
            }
        }

        if (j >= p.Length)
            return;

        var sb = new StringBuilder(filename.Length + 6 * p.Length + 32);

        sb.Append($"{filename} offset {j}: >>>");

        foreach (var ch in p)
        {
            if (ch >= 0x20 && ch < 0x7f)
                sb.Append(ch);
            else
                sb.Append($"\\u{(uint)ch:X04}");
        }

        sb.Append("<<<");

        Console.WriteLine(sb.ToString());

        Interlocked.Increment(ref _invalidFiles);
    }
}