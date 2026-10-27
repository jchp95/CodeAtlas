using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class HttpFileParser
{
    private static readonly Regex RequestLine = new(@"^\s*(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|TRACE)\s+(\S+)(?:\s+HTTP/[\d.]+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VariableLine = new(@"^\s*@([A-Za-z0-9_.-]+)\s*=\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex VariableToken = new(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled);

    public static ParsedHttpRequest[] Parse(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var markers = new List<(int Line, string Title)>();
        for (var i = 0; i < lines.Length; i++)
            if (Regex.IsMatch(lines[i], @"^\s*###")) markers.Add((i, lines[i].Trim().TrimStart('#').Trim()));
        var sections = new List<(int Start, int End, string Title)>();
        if (markers.Count == 0) sections.Add((0, lines.Length, ""));
        else
        {
            if (markers[0].Line > 0) sections.Add((0, markers[0].Line, ""));
            for (var i = 0; i < markers.Count; i++)
                sections.Add((markers[i].Line + 1, i + 1 < markers.Count ? markers[i + 1].Line : lines.Length, markers[i].Title));
        }
        var globalVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var match = VariableLine.Match(line);
            if (match.Success) globalVariables[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }
        var result = new List<ParsedHttpRequest>();
        foreach (var section in sections)
        {
            var requestIndex = -1; Match? requestMatch = null;
            for (var line = section.Start; line < section.End; line++)
            {
                var match = RequestLine.Match(lines[line]);
                if (match.Success) { requestIndex = line; requestMatch = match; break; }
            }
            if (requestIndex < 0 || requestMatch is null) continue;
            var headerEnd = requestIndex + 1;
            while (headerEnd < section.End && !string.IsNullOrWhiteSpace(lines[headerEnd])) headerEnd++;
            var headers = lines.Skip(requestIndex + 1).Take(headerEnd - requestIndex - 1)
                .Where(line => !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(line => line.Contains(':'));
            var body = headerEnd < section.End ? string.Join("\n", lines.Skip(headerEnd + 1).Take(section.End - headerEnd - 1)).TrimEnd() : "";
            var name = section.Title;
            if (string.IsNullOrWhiteSpace(name))
            {
                var preceding = lines.Skip(Math.Max(0, requestIndex - 3)).Take(Math.Min(3, requestIndex));
                var nameMatch = preceding.Select(line => Regex.Match(line, @"#\s*@name\s+(.+)$", RegexOptions.IgnoreCase))
                    .FirstOrDefault(match => match.Success);
                if (nameMatch is not null && nameMatch.Success) name = nameMatch.Groups[1].Value.Trim();
            }
            var variables = new Dictionary<string, string>(globalVariables, StringComparer.OrdinalIgnoreCase);
            result.Add(new ParsedHttpRequest(result.Count + 1,
                string.IsNullOrWhiteSpace(name) ? $"{requestMatch.Groups[1].Value.ToUpperInvariant()} {requestMatch.Groups[2].Value}" : name,
                requestMatch.Groups[1].Value.ToUpperInvariant(), requestMatch.Groups[2].Value,
                string.Join("\n", headers), body, requestIndex + 1, variables));
        }
        return result.ToArray();
    }

    public static string PathForMatch(string rawUrl, IReadOnlyDictionary<string, string> variables)
    {
        var value = ReplaceVariables(rawUrl, variables, leaveUnknown: false);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri.AbsolutePath;
        value = Regex.Replace(value, @"^(?:\{\{[^}]+\}\})+", "");
        if (!value.StartsWith('/'))
        {
            var slash = value.IndexOf('/');
            if (slash >= 0) value = value[slash..];
        }
        var query = value.IndexOf('?'); if (query >= 0) value = value[..query];
        return value.Length == 0 ? "/" : value;
    }

    public static string ReplaceVariables(string value, IReadOnlyDictionary<string, string> variables, bool leaveUnknown = true)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            var changed = false;
            value = VariableToken.Replace(value, match =>
            {
                if (!variables.TryGetValue(match.Groups[1].Value, out var replacement)) return leaveUnknown ? match.Value : "";
                changed = true; return replacement;
            });
            if (!changed) break;
        }
        return value;
    }
}
