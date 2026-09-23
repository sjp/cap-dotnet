using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Cap.Stress.Tests;

/// <summary>
/// An attacker in a process of its own, swapping names until it is killed.
/// </summary>
/// <remarks>
/// <para>
/// The attackers in the other races are threads in the process under test, which is the
/// convenient arrangement and not the realistic one: whoever writes inside a sandbox is almost
/// always some other program. The kernel does not care which process made a change, so nothing
/// about containment should differ, but a thread shares the process's scheduler, its descriptor
/// table and its locks with the code it is attacking, and a result that depended on any of that
/// would be a result about the harness. This runs the sharpest of the races with the attacker
/// outside, to show that nothing does.
/// </para>
/// <para>
/// It answers before the test platform starts, since it is not running a suite, and never
/// finishes on its own: the parent kills it when the race is over.
/// </para>
/// </remarks>
internal static class AdversaryChild
{
    /// <summary>Set to the name to swap, which makes this process the attacker.</summary>
    public const string SlotVariable = "CAPDOTNET_STRESS_ADVERSARY_SLOT";

    /// <summary>Set to the names to swap it with in turn, separated by the path-list separator.</summary>
    public const string PartnersVariable = "CAPDOTNET_STRESS_ADVERSARY_PARTNERS";

    /// <summary>The line the attacker prints once it has made its first full cycle.</summary>
    public const string Ready = "adversary: ready";

    [ModuleInitializer]
    internal static void AttackIfRequested()
    {
        string? slot = Environment.GetEnvironmentVariable(SlotVariable);
        if (string.IsNullOrEmpty(slot))
        {
            return;
        }

        string[] partners = Environment.GetEnvironmentVariable(PartnersVariable)!.Split(Path.PathSeparator);
        bool announced = false;
        while (true)
        {
            foreach (string partner in partners)
            {
                try
                {
                    HostOps.Exchange(slot, partner);
                    HostOps.Exchange(slot, partner);
                }
                catch (IOException)
                {
                    // Lost to the code under test; try the next.
                }
            }

            if (!announced)
            {
                Console.Out.WriteLine(Ready);
                Console.Out.Flush();
                announced = true;
            }
        }
    }
}

/// <summary>
/// The parent's end of an attacker running in another process.
/// </summary>
internal sealed class ProcessAdversary : Adversary
{
    private readonly Process _child;
    private bool _stopped;

    /// <summary>Starts the attacker and waits until it is attacking.</summary>
    public ProcessAdversary(string slot, params string[] partners)
    {
        _child = ChildProcess.Start(new Dictionary<string, string>
        {
            [AdversaryChild.SlotVariable] = slot,
            [AdversaryChild.PartnersVariable] = string.Join(Path.PathSeparator, partners),
        });

        string? line = _child.StandardOutput.ReadLine();
        if (line != AdversaryChild.Ready)
        {
            _child.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"The attacking process did not start attacking: '{line}' {_child.StandardError.ReadToEnd()}");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns no count: the attacker is killed rather than asked to stop, since a process
    /// swapping names in a tight loop is not listening for anything, and a killed process
    /// reports nothing.
    /// </remarks>
    public override long? Stop()
    {
        if (!_stopped)
        {
            _stopped = true;
            _child.Kill(entireProcessTree: true);
            _child.WaitForExit();
            _child.Dispose();
        }

        return null;
    }
}
