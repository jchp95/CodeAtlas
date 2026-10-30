// SEEDED DEFECTS: this fixture deliberately contains the problems the interprocedural rules must find (it even
// produces compiler warnings). Fixing them makes tests/call-graph-test.sh fail; see the ATL31xx comments.
namespace Interproc;

public class Gateway
{
    private readonly HttpClient _http = new();

    // Blocking wait and HTTP call in one synchronous method.
    public string Fetch(string url) => _http.GetStringAsync(url).GetAwaiter().GetResult();

    public async Task<string> FetchAsync(string url, CancellationToken ct) => await _http.GetStringAsync(url, ct);

    // Calls an API that could take a token, but has no token of its own to pass.
    public Task<string> FetchNoToken(string url) => _http.GetStringAsync(url);

    public int Pure(int value) => value * 2;
}

public class Service
{
    private readonly Gateway _gateway = new();

    // ATL3101: asynchronous member -> synchronous helper -> blocking wait.
    public async Task<string> HandleAsync(string url)
    {
        await Task.Yield();
        return Helper(url);
    }

    private string Helper(string url) => _gateway.Fetch(url);

    // Negative: awaits an async method instead of a blocking helper.
    public async Task<string> HandleAsyncOk(string url, CancellationToken ct) => await _gateway.FetchAsync(url, ct);

    // ATL3102: a loop calls a method that performs an HTTP request.
    public void LoopOverUrls(IEnumerable<string> urls)
    {
        foreach (var url in urls) Load(url);
    }

    private void Load(string url) => _gateway.Fetch(url);

    // Negative: the loop only calls a pure method.
    public int LoopPure(IEnumerable<int> values)
    {
        var total = 0;
        foreach (var value in values) total += _gateway.Pure(value);
        return total;
    }

    // ATL3103: the token is lost in Middle, which then calls an API that accepts one.
    public async Task<string> WithToken(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await Middle(url);
    }

    private Task<string> Middle(string url) => _gateway.FetchNoToken(url);

    // Negative: the token reaches the end of the chain.
    public async Task<string> WithTokenOk(string url, CancellationToken ct) => await _gateway.FetchAsync(url, ct);
}

public static class Factory
{
    public static FileStream Open(string path)
    {
        return new FileStream(path, FileMode.Open);
    }
}

public class Consumer
{
    // ATL3104: the caller owns the returned stream and never disposes it.
    public int Leak(string path)
    {
        var stream = Factory.Open(path);
        return stream.ReadByte();
    }

    // ATL3104: result discarded.
    public void Discard(string path)
    {
        Factory.Open(path);
    }

    // Negative: disposed.
    public int Fine(string path)
    {
        using var stream = Factory.Open(path);
        return stream.ReadByte();
    }

    public string? Find(string key) => key.Length > 3 ? key : null;

    // ATL3105: the compiler flags a possible null from Find at the dereference.
    public int UseUnchecked(string key)
    {
        var value = Find(key);
        return value.Length;
    }

    // Negative: null is handled.
    public int UseChecked(string key)
    {
        var value = Find(key);
        return value?.Length ?? 0;
    }
}
