using System.Diagnostics;

namespace Cap.Tests;

/// <summary>What a finished child wrote.</summary>
internal readonly record struct ChildOutput(string Output, string Errors);

/// <summary>
/// Waits on a process a test started, for a bounded time.
/// </summary>
/// <remarks>
/// <para>
/// A blocking read of a child's output, or a wait for it to exit, lasts as long as the child
/// does. A child that stalls then holds the test, and with it the assembly, until the CI job
/// is killed, and the log names nothing. Every wait here gives up after a time instead: the
/// child and anything it started are killed, and the wait throws with what the child wrote to
/// its error stream, so the test that started it fails and says why.
/// </para>
/// <para>
/// Both streams are read asynchronously from the start and only the wait is bounded. Reading
/// one stream to its end before the other, or waiting before reading at all, deadlocks as soon
/// as the child fills the pipe nobody is reading.
/// </para>
/// </remarks>
internal static class ChildProcessWait
{
    /// <summary>Long enough for any child the suite starts; a stalled one is ended after this.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the streams are given to close once the child has exited or been killed. A
    /// grandchild that inherited them and outlived the child would otherwise hold the read open.
    /// </summary>
    private static readonly TimeSpan StreamGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads both of the child's redirected streams to their end and waits for it to exit.
    /// </summary>
    /// <param name="child">A process started with standard output and error redirected.</param>
    /// <param name="what">What the child is, for the message when it does not finish.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when null.</param>
    /// <exception cref="TimeoutException">The child did not exit in time, and was killed.</exception>
    public static ChildOutput Finish(Process child, string what, TimeSpan? timeout = null) =>
        FinishAsync(child, what, timeout, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc cref="Finish"/>
    /// <param name="cancellationToken">Ends the wait early, killing the child, when the run is cancelled.</param>
    public static async Task<ChildOutput> FinishAsync(
        Process child,
        string what,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan bound = timeout ?? DefaultTimeout;
        Task<string> output = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errors = child.StandardError.ReadToEndAsync(CancellationToken.None);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(bound);
        try
        {
            await child.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(child);
            string written = await Collect(output).ConfigureAwait(false);
            string failed = await Collect(errors).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw Expired(what, "finish", bound, $"Standard output:\n{written}\nStandard error:\n{failed}");
        }

        return new ChildOutput(await Collect(output).ConfigureAwait(false), await Collect(errors).ConfigureAwait(false));
    }

    /// <summary>Reads one line of the child's standard output.</summary>
    /// <param name="child">A process started with standard output and error redirected.</param>
    /// <param name="what">What the child is, for the message when it does not answer.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when null.</param>
    /// <returns>The line, or null when the child closed its output without writing one.</returns>
    /// <exception cref="TimeoutException">No line came in time, and the child was killed.</exception>
    public static string? ReadLine(Process child, string what, TimeSpan? timeout = null)
    {
        TimeSpan bound = timeout ?? DefaultTimeout;
        Task<string?> line = child.StandardOutput.ReadLineAsync();
        if (line.Wait(bound))
        {
            return line.Result;
        }

        Kill(child);
        string failed = Collect(child.StandardError.ReadToEndAsync()).GetAwaiter().GetResult();
        throw Expired(what, "answer", bound, $"Standard error:\n{failed}");
    }

    /// <summary>Kills the child and everything it started, and waits a bounded time for it to go.</summary>
    /// <exception cref="TimeoutException">The child was still there after the wait.</exception>
    public static void Kill(Process child, string what)
    {
        Kill(child);
        if (!child.WaitForExit(StreamGrace))
        {
            throw Expired(what, "exit after being killed", StreamGrace, string.Empty);
        }
    }

    private static void Kill(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the timeout and the kill.
        }
    }

    /// <summary>What a stream held, or a note saying it did not close, never waiting past the grace.</summary>
    private static async Task<string> Collect(Task<string> stream)
    {
        try
        {
            return await stream.WaitAsync(StreamGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return $"(the stream did not close within {StreamGrace.TotalSeconds}s)";
        }
    }

    private static TimeoutException Expired(string what, string verb, TimeSpan bound, string detail) =>
        new($"{what} did not {verb} within {bound.TotalSeconds}s and was killed.\n{detail}".TrimEnd());
}
