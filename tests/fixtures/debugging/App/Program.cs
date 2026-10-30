namespace DebugApp;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("wait"))
        {
            // Used by the attach test: stay alive long enough to be attached to.
            Console.WriteLine("waiting");
            Thread.Sleep(60_000);
        }

        var total = 0;
        for (var i = 1; i <= 5; i++)
        {
            total += Work.Square(i);
        }
        try
        {
            Work.Fail("handled");
        }
        catch (InvalidOperationException)
        {
            total += 100;
        }
        var secret = "hunter2";
        Console.WriteLine($"total={total} secret-length={secret.Length}");
        return 0;
    }
}

public static class Work
{
    public static int Square(int n)
    {
        var result = n * n;
        return result;
    }

    public static void Fail(string reason)
    {
        throw new InvalidOperationException(reason);
    }
}
