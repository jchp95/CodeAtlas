using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class HttpRequestExecutor
{
    private const int MaxResponseBytes = 512 * 1024;
    private static readonly Regex VariableToken = new(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled);

    public static async Task<HttpExecutionResult> ExecuteRawAsync(string method, string url, string headersText, string body)
    {
        var timer = Stopwatch.StartNew();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Failure(url, 0, "La URL debe ser absoluta y usar HTTP o HTTPS.");
        if (!HttpMethodNames.IsAllowed(method)) return Failure(url, 0, $"Método HTTP no válido: {method}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);
        var headers = ParseHeaders(headersText);
        var contentHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5", "Content-Range", "Expires", "Last-Modified" };
        var contentHeaders = headers.Where(header => contentHeaderNames.Contains(header.Key)).ToArray();
        var requestHeaders = headers.Where(header => !contentHeaderNames.Contains(header.Key)).ToArray();
        if (body.Length > 0 || contentHeaders.Length > 0)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            foreach (var header in contentHeaders)
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (MediaTypeHeaderValue.TryParse(header.Value, out var mediaType)) request.Content.Headers.ContentType = mediaType;
                }
                else request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        foreach (var header in requestHeaders) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var (responseBody, truncated) = await ReadBounded(response.Content, MaxResponseBytes, timeout.Token);
            timer.Stop();
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => string.Join(", ", group.SelectMany(pair => pair.Value)), StringComparer.OrdinalIgnoreCase);
            return new HttpExecutionResult(true, (int)response.StatusCode, response.ReasonPhrase ?? "", timer.ElapsedMilliseconds,
                response.RequestMessage?.RequestUri?.ToString() ?? url, responseHeaders, responseBody, truncated, null);
        }
        catch (OperationCanceledException) { timer.Stop(); return Failure(url, timer.ElapsedMilliseconds, "La petición o la lectura de respuesta superó el timeout de 30 segundos."); }
        catch (Exception ex) { timer.Stop(); return Failure(url, timer.ElapsedMilliseconds, ex.Message); }
    }

    public static async Task<HttpExecutionResult> ExecuteFileAsync(string root, string relativeFile, int requestIndex,
        string environmentName, string overridesBase64)
    {
        var fullRoot = Path.GetFullPath(root);
        var file = Path.GetFullPath(Path.Combine(fullRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(file, fullRoot) || !File.Exists(file)) return Failure(relativeFile, 0, "El archivo .http no existe dentro del workspace seleccionado.");
        var parsed = HttpFileParser.Parse(file).FirstOrDefault(request => request.Index == requestIndex);
        if (parsed is null) return Failure(relativeFile, 0, $"No se encontró la petición #{requestIndex} en el archivo .http.");
        HttpRequestOverrides overrides;
        try { overrides = JsonSerializer.Deserialize<HttpRequestOverrides>(Encoding.UTF8.GetString(Convert.FromBase64String(overridesBase64)), JsonDefaults.Options) ?? new(null, null, null, null); }
        catch { overrides = new(null, null, null, null); }

        var env = HttpEnvironmentLoader.LoadSelectedEnvironment(fullRoot, Path.GetDirectoryName(file)!, environmentName);
        var values = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parsed.FileVariables) values[pair.Key] = pair.Value; // File-scoped variables override selected environment.
        var method = overrides.Method ?? parsed.Method;
        var rawUrl = overrides.Url ?? parsed.Url;
        var rawHeaders = overrides.HeadersText ?? parsed.HeadersText;
        var rawBody = overrides.Body ?? parsed.Body;
        var url = HttpFileParser.ReplaceVariables(rawUrl, values);
        var headers = HttpFileParser.ReplaceVariables(rawHeaders, values);
        var body = HttpFileParser.ReplaceVariables(rawBody, values);
        var unresolved = VariableToken.Matches(url + "\n" + headers + "\n" + body).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unresolved.Length > 0) return Failure(url, 0,
            "Faltan variables HTTP: " + string.Join(", ", unresolved) + ". Configúralas en http-client.env.json o en el archivo .http.");
        return await ExecuteRawAsync(method, url, headers, body);
    }

    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':'); if (colon <= 0) continue;
            values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return values;
    }
    private static async Task<(string Body, bool Truncated)> ReadBounded(HttpContent content, int max, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(); var buffer = new byte[16 * 1024]; var remaining = max + 1;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read <= 0) break;
            output.Write(buffer, 0, read); remaining -= read;
        }
        var bytes = output.ToArray(); var truncated = bytes.Length > max;
        if (truncated) bytes = bytes[..max];
        return (Encoding.UTF8.GetString(bytes), truncated);
    }
    private static HttpExecutionResult Failure(string url, long ms, string error) =>
        new(false, 0, "", ms, url, new Dictionary<string, string>(), "", false, error);
    private static bool IsWithin(string path, string root) => path.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
