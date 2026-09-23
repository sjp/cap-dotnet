using System.Diagnostics.Metrics;
using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Tests;

/// <summary>
/// That the backend a process resolves through can be read from inside it and watched from
/// outside it.
/// </summary>
/// <remarks>
/// A process that runs on the weaker backend while its operator believes it is on the
/// stronger one has a narrower guarantee than anybody knows about. Reporting is the only
/// defence against that, so the report itself is tested: that it names the backend actually
/// in use, and that the counts behind it move when resolution happens.
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class ResolutionBackendReportingTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void The_reported_backend_is_the_one_resolution_uses()
    {
        ResolutionBackend reported = Dir.ResolutionBackend;

        Assert.NotEqual(ResolutionBackend.None, reported);
        Assert.Equal(PlatformOps.Current.Capabilities.Backend, reported);
    }

    [Fact]
    public void The_reported_backend_is_the_one_this_platform_can_have()
    {
        ResolutionBackend reported = Dir.ResolutionBackend;

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ResolutionBackend.WindowsRelativeOpen, reported);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(ResolutionBackend.PortableWalk, reported);
        }
        else
        {
            Assert.Contains(reported, new[] { ResolutionBackend.ConfinedOpen, ResolutionBackend.PortableWalk });
        }
    }

    /// <summary>
    /// The meter names the same backend, and its counts account for a nested resolution.
    /// </summary>
    /// <remarks>
    /// Either count may be the one that moves, depending on the backend, but one of them
    /// must: a nested path resolved with neither moving would mean resolution happened
    /// somewhere the report cannot see.
    /// </remarks>
    [Fact]
    public void The_meter_names_the_backend_and_counts_resolution()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b", "c"));
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        MeterSnapshot before = MeterSnapshot.Take();
        using (root.OpenDir("a/b/c"))
        {
        }

        MeterSnapshot after = MeterSnapshot.Take();

        Assert.Equal(Dir.ResolutionBackend.ToString(), after.Backend);
        long confined = after.ConfinedOpenAttempts - before.ConfinedOpenAttempts;
        long walked = after.ComponentOpens - before.ComponentOpens;

        if (Dir.ResolutionBackend == ResolutionBackend.ConfinedOpen)
        {
            Assert.Equal(1, confined);
            Assert.Equal(0, walked);
        }
        else
        {
            Assert.Equal(0, confined);
            Assert.True(walked >= 3, $"A path of three names was resolved in {walked} opens.");
        }
    }

    private sealed record MeterSnapshot(
        string? Backend,
        long ConfinedOpenAttempts,
        long ConfinedOpenRaceRetries,
        long ComponentOpens)
    {
        public static MeterSnapshot Take()
        {
            string? backend = null;
            long attempts = -1, retries = -1, components = -1;

            using MeterListener listener = new();
            listener.InstrumentPublished = static (instrument, l) =>
            {
                if (instrument.Meter.Name == ResolutionMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
            {
                if (instrument.Name == ResolutionMetrics.BackendInstrument)
                {
                    foreach (KeyValuePair<string, object?> tag in tags)
                    {
                        if (tag.Key == ResolutionMetrics.BackendTag)
                        {
                            backend = tag.Value as string;
                        }
                    }
                }
            });

            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                switch (instrument.Name)
                {
                    case ResolutionMetrics.ConfinedOpenAttemptsInstrument:
                        attempts = value;
                        break;
                    case ResolutionMetrics.ConfinedOpenRaceRetriesInstrument:
                        retries = value;
                        break;
                    case ResolutionMetrics.ComponentOpensInstrument:
                        components = value;
                        break;
                }
            });

            listener.Start();
            listener.RecordObservableInstruments();

            Assert.True(attempts >= 0 && retries >= 0 && components >= 0, "Not every instrument was published.");
            return new MeterSnapshot(backend, attempts, retries, components);
        }
    }
}
