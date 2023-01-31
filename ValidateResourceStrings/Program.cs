// Cribbed from https://devblogs.microsoft.com/oldnewthing/20190701-00/?p=102636

using Microsoft.Extensions.FileSystemGlobbing;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var detector = new MojibakeDetector();

var matcher = new Matcher();
matcher.AddIncludePatterns(args);

var currentDirectory = Directory.GetCurrentDirectory();

Parallel.ForEach(
    matcher.GetResultsInFullPath(currentDirectory)
    .Select(p => Path.GetRelativePath(currentDirectory, p))
    .Distinct(StringComparer.Ordinal),
    ValidateFile);

return detector.InvalidFiles > 0 ? 1 : 0;

void ValidateFile(string filename)
{
    const string RT_STRING = "#6";   // How do we get this from C#/Win32..?

    var dataFileFlag = Windows.Win32.System.LibraryLoader.LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_AS_DATAFILE;

    using var module = PInvoke.LoadLibraryEx(filename, dataFileFlag);
    if (null == module)
        throw new FileNotFoundException($"Unable to open {filename}");

    unsafe
    {
        if (!PInvoke.EnumResourceNames(module, RT_STRING, (module, type, name, param) => NamedResourceValidator(module, type, name, filename), 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0)
                throw new System.ComponentModel.Win32Exception(error);
        }
    }
}

unsafe bool NamedResourceValidator(Windows.Win32.Foundation.HINSTANCE module, Windows.Win32.Foundation.PCWSTR lpszType, Windows.Win32.Foundation.PCWSTR lpszName, string filename)
{
    var resourceHandle = PInvoke.FindResource(module, lpszName, lpszType);
    var resource = PInvoke.LoadResource(module, resourceHandle);
    var size = PInvoke.SizeofResource(module, resourceHandle);

    var lockedResource = (char*)PInvoke.LockResource(resource);
    if (null == lockedResource)
    {
        int error = Marshal.GetLastWin32Error();
        throw new System.ComponentModel.Win32Exception(error, $"Can't lock resource {resource} in file {filename}");
    }

    var buffer = new Span<char>(lockedResource, checked((int)size));

    detector.ResourceValidator(filename, buffer);

    return true;
}

class MojibakeDetector
{
    static readonly Dictionary<char, byte> To1252Table = new Dictionary<char, byte>(Init1252Table());

    int invalidFiles = 0;

    public int InvalidFiles => invalidFiles;

    public void ResourceValidator(string filename, Span<char> buffer)
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
            yield return new KeyValuePair<char, byte>(encoded[i], code);
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

    void ValidateString(string filename, Span<char> p)
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
            else
            {
                var ch = To1252(p[j]);
                if (ch > 0x7f)
                {
                    // Does this look like UTF-8?
                    if ((ch & 0xE0) == 0xC0
                        && (j + 1 < p.Length && (To1252(p[j + 1]) & 0xC0) == 0x80))
                    {
                        break;
                    }
                    if ((ch & 0xf0) == 0xE0
                        && (j + 1 < p.Length && (To1252(p[j + 1]) & 0xC0) == 0x80)
                        && (j + 2 < p.Length && (To1252(p[j + 2]) & 0xC0) == 0x80))
                    {
                        break;
                    }
                    if ((ch & 0xf8) == 0xF0
                        && (j + 1 < p.Length && (To1252(p[j + 1]) & 0xC0) == 0x80)
                        && (j + 2 < p.Length && (To1252(p[j + 2]) & 0xC0) == 0x80)
                        && (j + 3 < p.Length && (To1252(p[j + 3]) & 0xC0) == 0x80))
                    {
                        break;
                    }
                }
            }
        }

        if (j >= p.Length)
            return;

        var sb = new StringBuilder();

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

        Interlocked.Increment(ref invalidFiles);
    }
}