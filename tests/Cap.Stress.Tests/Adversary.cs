namespace Cap.Stress.Tests;

/// <summary>
/// Something changing the tree for as long as a race runs.
/// </summary>
internal abstract class Adversary : IDisposable
{
    /// <summary>
    /// Stops the attack and reports how many full cycles it made, or <see langword="null"/>
    /// when it cannot say.
    /// </summary>
    /// <exception cref="Exception">Whatever stopped the attack other than being asked to.</exception>
    public abstract long? Stop();

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (InvalidOperationException)
        {
            // Stopped on the way out of a test that has already failed for some other reason,
            // which is the reason worth reporting.
        }
    }
}

/// <summary>
/// An attacker on a thread of its own, repeating one cycle of moves until told to stop.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated thread rather than a task, so that it cannot be starved by the pool it would
/// otherwise share with whatever the test is doing, and so that it is genuinely concurrent with
/// the resolution rather than taking turns with it.
/// </para>
/// <para>
/// A move that fails is not a fault. The attacker is fighting both the code under test and its
/// own previous moves — a name it meant to swap may have just been removed by the operation it
/// is attacking — so failing to make a move is part of the race, and it simply tries the next.
/// Anything other than a filesystem refusal is a fault in the test, and is rethrown when the
/// attack is stopped.
/// </para>
/// </remarks>
internal sealed class ThreadAdversary : Adversary
{
    private readonly Action _cycle;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private long _cycles;
    private Exception? _fault;
    private bool _stopped;

    /// <summary>Starts the attack.</summary>
    /// <param name="cycle">One full cycle of moves, which leaves the tree as it found it.</param>
    public ThreadAdversary(Action cycle)
    {
        _cycle = cycle;
        _thread = new Thread(Run) { IsBackground = true, Name = "adversary" };
        _thread.Start();
    }

    /// <inheritdoc/>
    public override long? Stop()
    {
        if (!_stopped)
        {
            _stopped = true;
            _stopping = true;
            _thread.Join();
        }

        if (_fault is not null)
        {
            throw new InvalidOperationException("The adversary failed for a reason other than losing a race.", _fault);
        }

        return Interlocked.Read(ref _cycles);
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                try
                {
                    _cycle();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A move lost to the code under test, or to the attacker's own last move.
                }

                Interlocked.Increment(ref _cycles);
            }
        }
        catch (Exception e)
        {
            _fault = e;
        }
    }
}
