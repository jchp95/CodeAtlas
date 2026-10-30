using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class PathTools
{
    public static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    public static bool IsWithin(string file, string directory) =>
        file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Resolves symbolic links in every component (macOS temp folders live behind /var → /private/var). Debug adapters match
    /// breakpoints to the path recorded in the PDB, which is the resolved one, so an unresolved path binds nothing.
    /// </summary>
    public static string Canonical(string path)
    {
        try
        {
            var current = Path.GetPathRoot(path) ?? "/";
            foreach (var part in path[current.Length..].Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                var info = new DirectoryInfo(next);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target) next = target.FullName;
                current = next;
            }
            return current;
        }
        catch { return path; }
    }
}
