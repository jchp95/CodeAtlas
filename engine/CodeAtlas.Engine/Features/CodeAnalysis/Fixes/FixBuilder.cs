using System.Security.Cryptography;
using System.Text;

static class FixBuilder
{
    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static SuggestedFix Build(string title, string file, string originalText, TextEdit[] edits)
    {
        var updated = ApplyEdits(originalText, edits);
        return new SuggestedFix(title, file, HashText(originalText), edits, UnifiedDiff(file, originalText, updated));
    }

    public static string ApplyEdits(string text, IEnumerable<TextEdit> edits)
    {
        var builder = new StringBuilder(text);
        // Apply from the end so earlier offsets stay valid.
        foreach (var edit in edits.OrderByDescending(e => e.Offset))
        {
            if (edit.Offset < 0 || edit.Length < 0 || edit.Offset + edit.Length > builder.Length)
                throw new InvalidOperationException($"Edit at offset {edit.Offset} is outside the file.");
            builder.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);
        }
        return builder.ToString();
    }

    /// <summary>Single-hunk unified diff with three lines of context; sufficient for the localized edits fixes produce.</summary>
    public static string UnifiedDiff(string file, string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        const int context = 3;
        var start = Math.Max(0, prefix - context);
        var endA = Math.Min(a.Length, a.Length - suffix + context);
        var endB = Math.Min(b.Length, b.Length - suffix + context);
        var diff = new StringBuilder();
        diff.Append($"--- a/{file}\n+++ b/{file}\n@@ -{start + 1},{endA - start} +{start + 1},{endB - start} @@\n");
        for (var i = start; i < prefix; i++) diff.Append(' ').Append(a[i].TrimEnd('\r')).Append('\n');
        for (var i = prefix; i < a.Length - suffix; i++) diff.Append('-').Append(a[i].TrimEnd('\r')).Append('\n');
        for (var i = prefix; i < b.Length - suffix; i++) diff.Append('+').Append(b[i].TrimEnd('\r')).Append('\n');
        for (var i = a.Length - suffix; i < endA; i++) diff.Append(' ').Append(a[i].TrimEnd('\r')).Append('\n');
        return diff.ToString();
    }

    /// <summary>
    /// Applies a fix only if the file is byte-for-byte what the analysis saw. Returns an error message
    /// instead of throwing when the user changed the file, so their edits are never clobbered.
    /// </summary>
    public static string? TryApply(string repositoryRoot, SuggestedFix fix)
    {
        var path = Path.GetFullPath(Path.Combine(repositoryRoot, fix.File));
        if (!path.StartsWith(Path.GetFullPath(repositoryRoot), StringComparison.Ordinal))
            return "The fix targets a file outside the repository and was refused.";
        if (!File.Exists(path)) return $"{fix.File} no longer exists.";
        var current = File.ReadAllText(path);
        if (HashText(current) != fix.BaseFileHash)
            return $"{fix.File} changed since the analysis; re-run the analysis before applying this fix.";
        File.WriteAllText(path, ApplyEdits(current, fix.Edits));
        return null;
    }
}
