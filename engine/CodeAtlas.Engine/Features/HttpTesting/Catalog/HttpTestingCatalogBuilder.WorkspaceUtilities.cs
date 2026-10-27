using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static partial class HttpTestingCatalogBuilder
{
private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop(); string[] files; string[] children;
            try { files = Directory.GetFiles(dir); children = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (Excluded.Contains(name) || (name.StartsWith('.') && name != ".well-known")) continue;
                stack.Push(child);
            }
        }
    }
}
