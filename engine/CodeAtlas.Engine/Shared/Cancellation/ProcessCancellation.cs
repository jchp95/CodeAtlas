using System.Runtime.InteropServices;

/// <summary>
/// Cancels on SIGINT or SIGTERM (the desktop app terminates its child process). The registration objects must stay
/// referenced: if they are collected the signal handler is silently unregistered and SIGTERM kills the process.
/// </summary>
sealed class ProcessCancellation : IDisposable
{
    private readonly CancellationTokenSource source = new();
    private readonly PosixSignalRegistration terminate;
    private readonly ConsoleCancelEventHandler interrupt;

    public ProcessCancellation()
    {
        interrupt = (_, e) => { e.Cancel = true; source.Cancel(); };
        Console.CancelKeyPress += interrupt;
        terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; source.Cancel(); });
    }

    public CancellationToken Token => source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= interrupt;
        terminate.Dispose();
        source.Dispose();
    }
}
