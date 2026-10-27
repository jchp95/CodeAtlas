using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class TypeNames
{
    public static string Short(string value)
    {
        var noGeneric = Regex.Replace(value, "<.*>", "");
        var parts = noGeneric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? noGeneric : parts[^1];
    }

    public static bool Match(string first, string second) =>
        string.Equals(Short(first), Short(second), StringComparison.Ordinal);
}
