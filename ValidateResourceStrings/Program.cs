using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.LibraryLoader;
using Microsoft.Extensions.FileSystemGlobbing;
using ValidateResourceStrings;

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
    var dataFileFlag = LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_AS_DATAFILE;

    using var module = PInvoke.LoadLibraryEx(filename, dataFileFlag);
    if (null == module)
        throw new FileNotFoundException($"Unable to open {filename}");

    if (!PInvoke.EnumResourceNames((HMODULE)module.DangerousGetHandle(), PInvoke.RT_STRING,
        (m, t, n, param) => NamedResourceValidator(m, t, n, filename), 0))
    {
        var error = Marshal.GetLastWin32Error();
        if (error != 0)
            throw new Win32Exception(error);
    }
}

unsafe bool NamedResourceValidator(HINSTANCE module, PCWSTR lpszType, PCWSTR lpszName, string filename)
{
    var resourceHandle = PInvoke.FindResource(module, lpszName, lpszType);
    var resource = PInvoke.LoadResource(module, resourceHandle);
    var size = PInvoke.SizeofResource(module, resourceHandle);

    var lockedResource = (char*)PInvoke.LockResource(resource);
    if (lockedResource is null)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"Can't lock resource {resource} in file {filename}");
    }

    var buffer = new ReadOnlySpan<char>(lockedResource, checked((int)size));

    detector.ResourceValidator(filename, buffer);

    return true;
}
