using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Minimal Debug Adapter Protocol client over a child process' stdio (Content-Length framing).</summary>
sealed class DapClient : IDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly BlockingCollection<JsonNode> events = new();
    private readonly object writeGate = new();
    private int sequence;

    public DapClient(string adapterPath, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(adapterPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        process = Process.Start(info) ?? throw new InvalidOperationException("The debug adapter could not be started.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        new Thread(ReadLoop) { IsBackground = true, Name = "dap-reader" }.Start();
    }

    public bool HasExited => process.HasExited;

    public JsonNode Request(string command, JsonObject? arguments, int timeoutMs = 20_000)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        Send(new JsonObject { ["seq"] = id, ["type"] = "request", ["command"] = command, ["arguments"] = arguments ?? new JsonObject() });
        if (!completion.Task.Wait(timeoutMs)) { pending.TryRemove(id, out _); throw new TimeoutException($"The debug adapter did not answer '{command}' within {timeoutMs} ms."); }
        var response = completion.Task.Result;
        if (response["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"'{command}' failed: {response["message"]?.GetValue<string>() ?? response["body"]?["error"]?["format"]?.GetValue<string>() ?? "no message"}");
        return response["body"] ?? new JsonObject();
    }

    /// <summary>Sends a request without waiting; some adapters answer launch only after configurationDone.</summary>
    public Task<JsonNode> RequestAsync(string command, JsonObject? arguments)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        Send(new JsonObject { ["seq"] = id, ["type"] = "request", ["command"] = command, ["arguments"] = arguments ?? new JsonObject() });
        return completion.Task;
    }

    public JsonNode? NextEvent(int timeoutMs) => events.TryTake(out var e, timeoutMs) ? e : null;

    private void Send(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        lock (writeGate)
        {
            var stream = process.StandardInput.BaseStream;
            stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            stream.Write(body);
            stream.Flush();
        }
    }

    private void ReadLoop()
    {
        var stream = process.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var length = -1;
                while (true)
                {
                    var line = ReadHeaderLine(stream);
                    if (line is null) return;
                    if (line.Length == 0) break;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                }
                if (length < 0 || length > 16 * 1024 * 1024) return; // bounded: a corrupt frame ends the session instead of allocating freely
                var buffer = new byte[length];
                for (var read = 0; read < length;)
                {
                    var n = stream.Read(buffer, read, length - read);
                    if (n <= 0) return;
                    read += n;
                }
                var message = JsonNode.Parse(buffer);
                if (message is null) continue;
                switch (message["type"]?.GetValue<string>())
                {
                    case "response":
                        if (pending.TryRemove(message["request_seq"]!.GetValue<int>(), out var completion)) completion.TrySetResult(message);
                        break;
                    case "event":
                        events.Add(message);
                        break;
                    case "request": // reverse requests (runInTerminal…) are not supported: answer so the adapter does not wait forever
                        Send(new JsonObject { ["seq"] = Interlocked.Increment(ref sequence), ["type"] = "response", ["request_seq"] = message["seq"]!.DeepClone(), ["success"] = false,
                            ["command"] = message["command"]!.DeepClone(), ["message"] = "not supported by this client" });
                        break;
                }
            }
        }
        catch { /* the process ended */ }
        finally { foreach (var p in pending.Values) p.TrySetException(new InvalidOperationException("The debug adapter ended.")); events.CompleteAdding(); }
    }

    private static string? ReadHeaderLine(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (b == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add((byte)b);
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }
}
