using System.Diagnostics;

sealed record ProcessOutcome(int? ExitCode, bool TimedOut, bool Cancelled, string[] Tail, double DurationMs);

/// <summary>Runs a child process with separate arguments (no shell), a deadline, cancellation, and bounded captured output.</summary>
static class BoundedProcess
{
    public static ProcessOutcome Run(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout,
        CancellationToken cancellation, int tailLines = 60)
    {
        var info = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var tail = new Queue<string>();
        var gate = new object();
        void Capture(string? line)
        {
            if (line is null) return;
            lock (gate) { tail.Enqueue(line.Length > 400 ? line[..400] + "…" : line); while (tail.Count > tailLines) tail.Dequeue(); }
        }

        var timer = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool timedOut = false, cancelled = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try { process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            cancelled = cancellation.IsCancellationRequested;
            timedOut = !cancelled;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.WaitForExit();
        }
        process.WaitForExit(); // flush the asynchronous readers
        lock (gate) return new ProcessOutcome(timedOut || cancelled ? null : process.ExitCode, timedOut, cancelled, tail.ToArray(), Math.Round(timer.Elapsed.TotalMilliseconds, 1));
    }
}
