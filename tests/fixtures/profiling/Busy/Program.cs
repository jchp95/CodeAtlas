namespace Busy;

// Workloads with a known shape, so a profiler's findings can be checked against what the code really does:
//   cpu        - CPU time is dominated by Workloads.HotLoop
//   alloc      - many short-lived allocations (high allocation rate, nothing retained)
//   leak       - allocations that stay reachable from a static list (retained growth)
//   contention - many threads fight for one lock
//   starve     - tasks that block thread-pool threads on async work (sync over async)
public static class Program
{
    public static int Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "cpu";
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 8;
        Console.WriteLine($"ready {mode} pid={Environment.ProcessId}");
        if (mode == "fixed")
        {
            // Work-bound (not time-bound) variant: the amount of CPU is proportional to the first argument, so two runs differ for a real reason.
            for (var i = 0; i < seconds; i++) Workloads.HotLoop(5_000_000);
            return 0;
        }

        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            switch (mode)
            {
                case "cpu": Workloads.HotLoop(2_000_000); Workloads.Cheap(); break;
                case "alloc": Workloads.Churn(2_000); break;
                case "leak": Workloads.Leak(200); Thread.Sleep(20); break;
                case "contention": Workloads.Contend(8, 60); break;
                case "starve": Workloads.Starve(64); break;
                default: return 2;
            }
        }
        Console.WriteLine("done");
        return 0;
    }
}

public static class Workloads
{
    private static readonly List<byte[]> Retained = new();
    private static readonly object Gate = new();
    private static long counter;
    // The array must escape: the runtime stack-allocates (or removes) arrays that never leave the method, which would hide the allocation.
    private static byte[]? sink;

    public static double HotLoop(int iterations)
    {
        var sum = 0.0;
        for (var i = 1; i <= iterations; i++) sum += Math.Sqrt(i) * Math.Sin(i);
        return sum;
    }

    public static int Cheap() => Environment.TickCount;

    public static int Churn(int count)
    {
        var total = 0;
        for (var i = 0; i < count; i++) { sink = new byte[4096]; total += sink.Length; }
        return total;
    }

    public static void Leak(int count)
    {
        for (var i = 0; i < count; i++) Retained.Add(new byte[8192]);
    }

    public static void Contend(int threads, int iterations)
    {
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < iterations; i++) { lock (Gate) { counter++; Thread.Sleep(2); } }
        })).ToArray();
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
    }

    public static void Starve(int tasks)
    {
        var work = Enumerable.Range(0, tasks).Select(_ => Task.Run(() => SlowAsync().GetAwaiter().GetResult())).ToArray();
        Task.WaitAll(work);
    }

    private static async Task<int> SlowAsync()
    {
        await Task.Delay(150);
        return 1;
    }
}
