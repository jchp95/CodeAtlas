namespace RiskyService;

// Every member below contains exactly one defect that the analyzer must report.
public class OrderService
{
    private readonly HttpClient _http = new();

    public string BlockingResult(Task<string> task) => task.Result;

    public void BlockingWait(Task task)
    {
        task.Wait();
    }

    public void BlockingAwaiter(Task task)
    {
        task.GetAwaiter().GetResult();
    }

    public async Task<int> BlockingInAsync(Task<int> task)
    {
        await Task.Delay(1);
        return task.Result;
    }

    public async void FireAndForget()
    {
        await Task.Delay(1);
    }

    public async Task NoToken(CancellationToken ct)
    {
        await Task.Delay(10);
    }

    public void EmptyCatch()
    {
        try { int.Parse("x"); }
        catch (Exception) { }
    }

    public int SilentCatch()
    {
        try { return int.Parse("x"); }
        catch (Exception) { return -1; }
    }

    public void ThrowEx()
    {
        try { int.Parse("x"); }
        catch (FormatException ex) { throw ex; }
    }

    public int Leak()
    {
        var stream = new FileStream("data.bin", FileMode.Open);
        return stream.ReadByte();
    }

    public async Task Loop(IEnumerable<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            await _http.GetStringAsync(url, ct);
        }
    }

    public int Twice(IEnumerable<int> items)
    {
        var count = items.Count();
        var sum = items.Sum();
        return count + sum;
    }

    public void InLoop(IEnumerable<int> items, IList<int> other)
    {
        foreach (var o in other)
        {
            if (items.Contains(o)) Console.WriteLine(o);
        }
    }

    public int Complex(int x)
    {
        if (x == 1) return 1;
        if (x == 2) return 2;
        if (x == 3) return 3;
        if (x == 4) return 4;
        if (x == 5) return 5;
        if (x == 6) return 6;
        return 0;
    }

    public int Long(int x)
    {
        var a = x + 1;
        var b = a + 1;
        var c = b + 1;
        var d = c + 1;
        var e = d + 1;
        var f = e + 1;
        var g = f + 1;
        var h = g + 1;
        var i = h + 1;
        var j = i + 1;
        var k = j + 1;
        return k;
    }
}
