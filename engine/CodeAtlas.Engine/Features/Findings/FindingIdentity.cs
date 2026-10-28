using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

static class FindingIdentity
{
    public static string Normalize(string code) => Regex.Replace(code, @"\s+", "");

    /// <summary>Stable across line shifts. <paramref name="ordinal"/> separates identical offending code inside one symbol.</summary>
    public static string Create(string ruleId, string? project, string file, string? symbol, string normalizedCode, int ordinal)
    {
        var key = string.Join('\u001f', ruleId, project ?? "", file, symbol ?? "", normalizedCode.Length > 240 ? normalizedCode[..240] : normalizedCode, ordinal);
        return "F-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];
    }
}
