namespace RiskyService;

// Correct counterparts of the defects: the analyzer must report nothing in this file.
public class CleanService
{
    private readonly HttpClient _http = new();

    public async Task<string> Good(string url, CancellationToken ct)
    {
        await Task.Delay(10, ct);
        return await _http.GetStringAsync(url, ct);
    }

    public async Task<int> WhenAllThenResult()
    {
        var a = Task.FromResult(1);
        var b = Task.FromResult(2);
        await Task.WhenAll(a, b);
        return a.Result + b.Result;
    }

    private async void OnClick(object? sender, EventArgs e)
    {
        await Task.Yield();
    }

    public void Rethrow()
    {
        try { int.Parse("x"); }
        catch (FormatException) { throw; }
    }

    public void Logged()
    {
        try { int.Parse("x"); }
        catch (Exception ex) { Console.WriteLine(ex.Message); }
    }

    public int Disposed()
    {
        using var stream = new FileStream("data.bin", FileMode.Open);
        return stream.ReadByte();
    }

    public int DisposedExplicitly()
    {
        var reader = new StreamReader("data.txt");
        var first = reader.Read();
        reader.Dispose();
        return first;
    }

    public Stream Escapes()
    {
        var stream = new FileStream("data.bin", FileMode.Open);
        return stream;
    }

    public int Materialized(IEnumerable<int> items)
    {
        var list = items.ToList();
        return list.Count + list.Sum();
    }

    public int Exclusive(IEnumerable<int> items, bool flag)
    {
        if (flag) return items.Count();
        else return items.Sum();
    }

    public int Reassigned(IEnumerable<int> items)
    {
        items = items.ToList();
        return items.Count() + items.Sum();
    }

    public int Simple(int x) => x > 0 ? 1 : 0;
}
