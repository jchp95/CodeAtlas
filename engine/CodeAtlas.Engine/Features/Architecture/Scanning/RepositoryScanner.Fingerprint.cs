using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

sealed partial class RepositoryScanner
{
public static string GetWorkspaceFingerprint(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var repositories = GitWorkspaceInspector.Scan(root);
        var material = new StringBuilder();
        foreach (var repository in repositories.OrderBy(repository => repository.Path, StringComparer.Ordinal))
        {
            material.Append(repository.Path).Append('|').Append(repository.Branch).Append('|').Append(repository.HeadCommit).AppendLine();
            foreach (var change in (repository.Changes ?? Array.Empty<GitChange>()).OrderBy(change => change.Path, StringComparer.Ordinal))
            {
                material.Append(change.Status).Append('|').Append(change.Path).Append('|');
                var changedFile = Path.GetFullPath(Path.Combine(root, change.Path));
                material.Append(FingerprintChangedFile(changedFile)).AppendLine();
            }
        }
        if (repositories.Length == 0)
        {
            // Git may be absent for a temporary folder. Include source/config file fingerprints,
            // using the same excluded-directory-aware walk as the scanner (never traverse node_modules/.git).
            IEnumerable<string> files = Directory.Exists(root)
                ? new RepositoryScanner(root).EnumerateFiles(root, "*")
                    .Where(file => Path.GetExtension(file) is ".cs" or ".csproj" or ".ts" or ".tsx" or ".js" or ".jsx" or ".json" or ".yaml" or ".yml")
                    .Take(20_000)
                    .Select(file =>
                    {
                        var info = new FileInfo(file);
                        return $"{Path.GetRelativePath(root, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                    })
                    .OrderBy(value => value, StringComparer.Ordinal)
                : Array.Empty<string>();
            material.AppendJoin('\n', files);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    private static string FingerprintChangedFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Directory.Exists(path) ? "directory" : "missing";
            using var stream = File.OpenRead(path);
            string contentHash;
            if (info.Length <= 8L * 1024 * 1024)
            {
                contentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            else
            {
                // Hash bounded samples of very large files to keep 15-second auto refresh responsive.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                var firstRead = stream.Read(buffer, 0, buffer.Length);
                hash.AppendData(buffer, 0, firstRead);
                stream.Seek(Math.Max(0, info.Length - buffer.Length), SeekOrigin.Begin);
                var lastRead = stream.Read(buffer, 0, buffer.Length);
                hash.AppendData(buffer, 0, lastRead);
                contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{contentHash}";
        }
        catch (Exception ex)
        {
            try
            {
                var info = new FileInfo(path);
                return $"metadata-only|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ex.GetType().Name}";
            }
            catch { return "unreadable"; }
        }
    }
}
