using System.Diagnostics.Metrics;

namespace Cap.Primitives.Interop;

/// <summary>
/// Publishes which resolution backend is active, and how much work each one has done, as
/// instruments on a <see cref="Meter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Which backend runs changes what the containment guarantee covers, so it has to be
/// observable from outside the process and not only by code that thinks to ask. A meter is
/// the channel a .NET operator already watches: the instruments reach
/// <c>dotnet-counters</c>, OpenTelemetry and any <see cref="MeterListener"/> without this
/// library defining a type of its own for them.
/// </para>
/// <para>
/// The counts are what make a demotion visible after the fact. A process that was expected
/// to resolve through the kernel-atomic open and shows confined-open attempts at zero and
/// component opens climbing is walking, whatever it was configured to do.
/// </para>
/// <para>
/// Every instrument is observable, read from counters the platform layer keeps anyway, so a
/// process with no listener pays nothing on the resolution path for any of this.
/// </para>
/// </remarks>
internal static class ResolutionMetrics
{
    /// <summary>The meter's name, which is what a listener subscribes to.</summary>
    public const string MeterName = "Cap.Primitives";

    /// <summary>The gauge naming the active backend.</summary>
    public const string BackendInstrument = "cap.resolution.backend";

    /// <summary>The tag on <see cref="BackendInstrument"/> that carries the backend's name.</summary>
    public const string BackendTag = "cap.resolution.backend.name";

    /// <summary>Confined, kernel-atomic opens attempted.</summary>
    public const string ConfinedOpenAttemptsInstrument = "cap.resolution.confined_open.attempts";

    /// <summary>Confined opens retried after the kernel reported a lost race.</summary>
    public const string ConfinedOpenRaceRetriesInstrument = "cap.resolution.confined_open.race_retries";

    /// <summary>Single-name opens beneath an existing handle.</summary>
    public const string ComponentOpensInstrument = "cap.resolution.component_opens";

    private static readonly Meter s_meter = CreateMeter();

    /// <summary>
    /// The backend this process resolves through, or <see cref="ResolutionBackend.None"/>
    /// on an operating system this library has no implementation for.
    /// </summary>
    /// <remarks>
    /// Checks the operating system before touching the platform layer, whose construction
    /// throws on an unsupported one: the question has an answer there too, and the answer is
    /// that nothing is available.
    /// </remarks>
    public static ResolutionBackend ActiveBackend =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? PlatformOps.Current.Capabilities.Backend
            : ResolutionBackend.None;

    /// <summary>
    /// Makes sure the meter and its instruments exist.
    /// </summary>
    /// <returns>Always true; the value exists so the call can initialise a static field.</returns>
    public static bool Publish() => s_meter is not null;

    private static Meter CreateMeter()
    {
        Meter meter = new(MeterName);

        meter.CreateObservableGauge(
            BackendInstrument,
            static () => new Measurement<int>(1, new KeyValuePair<string, object?>(BackendTag, ActiveBackend.ToString())),
            unit: null,
            description: "The resolution backend this process uses; the value is always 1 and the backend is the tag.");

        meter.CreateObservableCounter(
            ConfinedOpenAttemptsInstrument,
            static () => PlatformOps.Current.ConfinedOpenAttempts,
            unit: "{open}",
            description: "Confined, kernel-atomic opens attempted. Zero on a process that is walking.");

        meter.CreateObservableCounter(
            ConfinedOpenRaceRetriesInstrument,
            static () => PlatformOps.Current.ConfinedOpenRaceRetries,
            unit: "{retry}",
            description: "Confined opens retried after the kernel reported that resolution lost a race with a rename.");

        meter.CreateObservableCounter(
            ComponentOpensInstrument,
            static () => PlatformOps.Current.ComponentOpens,
            unit: "{open}",
            description: "Single-name opens beneath an existing handle: one per name on a walk, none on a confined open.");

        return meter;
    }
}
