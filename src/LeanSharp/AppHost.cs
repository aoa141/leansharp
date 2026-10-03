// Windows launchers that are real executables: copies of the .NET application host.
//
// Building a .NET program on Windows produces `<Program>.exe` next to `<Program>.dll`: the
// application host of the .NET SDK, with the name of the dll embedded in it. A copy of it whose
// embedded path is changed (to a path relative to the copy; the host resolves it against its own
// directory) starts that dll from anywhere. `<sysroot>\bin\lean.exe` is such a copy: the program
// recognizes the launcher by its own executable name (`lean`, `lake`, ...) and derives the
// sysroot from its location, like native Lean (see `LeanSysroot.LauncherArgs`).
//
// Executables that are moved without the program (Lake's build directories, its artifact cache)
// are *anchored*: the embedded path climbs to the drive's root with more `..\` than the
// executable can be deep (Windows stops `..` at the root) and continues with the absolute
// path of the program, so it names the same file wherever the executable is on that drive.
//
// The application host is native code, but it is part of the .NET SDK like `dotnet.exe`, and
// nothing is compiled here.

using System.Text;

namespace LeanSharp;

static class AppHost
{
    /// <summary>The size of the host's buffer for the embedded path, including the terminating NUL.</summary>
    const int PathSlotSize = 1024;

    /// <summary>
    /// `C:\x` for `\\?\C:\x` and `\\server\share` for `\\?\UNC\server\share`: in a program
    /// started by an application host, `AppContext.BaseDirectory` has that form.
    /// </summary>
    public static string WithoutExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
        return path;
    }

    /// <summary>
    /// The bytes of an application host at `exePath` that starts `assembly`, or null if there
    /// is none to copy (not Windows, no `<assembly>.exe`, or `exePath` on another drive).
    /// </summary>
    /// <param name="anchored">
    /// Refer to `assembly` by its absolute location (see the file comment) instead of its
    /// location relative to `exePath`.
    /// </param>
    public static byte[] Create(string assembly, string exePath, bool anchored = false)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string template = Path.ChangeExtension(assembly, ".exe");
            if (!File.Exists(template) || !File.Exists(assembly)) return null;
            string dir = Path.GetDirectoryName(Path.GetFullPath(WithoutExtendedPrefix(exePath)));
            string full = Path.GetFullPath(WithoutExtendedPrefix(assembly));
            string rel;
            if (anchored)
            {
                string root = Path.GetPathRoot(full);
                if (!string.Equals(root, Path.GetPathRoot(dir), StringComparison.OrdinalIgnoreCase)) return null;
                int depth = dir.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
                rel = string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, Math.Max(64, depth + 32))) + full.Substring(root.Length);
            }
            else
            {
                rel = Path.GetRelativePath(dir, full);
                if (Path.IsPathRooted(rel)) return null; // another drive: no relative path
            }
            byte[] path = Encoding.UTF8.GetBytes(rel);
            if (path.Length >= PathSlotSize) return null;
            byte[] data = File.ReadAllBytes(template);
            // the embedded name of the template's own dll, at the start of the buffer
            byte[] bound = Encoding.UTF8.GetBytes(Path.GetFileName(assembly) + "\0");
            int at = data.AsSpan().IndexOf(bound);
            if (at < 0 || data.AsSpan(at + 1).IndexOf(bound) >= 0 || at + PathSlotSize > data.Length) return null;
            for (int i = at + bound.Length; i < at + PathSlotSize; i++)
                if (data[i] != 0) return null; // not the zero-filled buffer of a bound host
            Array.Clear(data, at, PathSlotSize);
            path.CopyTo(data, at);
            return data;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Writes `bytes` to `path` unless it already has that content. A running executable cannot
    /// be replaced on Windows, but it can be renamed: it is moved aside and deleted later.
    /// </summary>
    public static void WriteIfChanged(string path, byte[] bytes)
    {
        // executables moved aside earlier, once they no longer run
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.old"))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return;
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                File.Move(path, path + "." + Environment.ProcessId + "." + Environment.TickCount64 + ".old");
                File.Move(tmp, path);
            }
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
