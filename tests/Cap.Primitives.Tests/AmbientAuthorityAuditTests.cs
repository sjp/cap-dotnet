namespace Cap.Primitives.Tests;

/// <summary>
/// The record of where a process took authority from outside itself.
/// </summary>
/// <remarks>
/// <para>
/// Searching the source for the acquisition call answers this question for code somebody
/// can read. The record answers it for the program that actually ran, which is a different
/// question once a dependency is involved: a library that takes authority inside itself is
/// not in the source anybody searched, and it is exactly the case an audit wants to find.
/// </para>
/// <para>
/// What is asserted here is the shape of the answer rather than its contents. One entry per
/// place, a count rather than a copy per occurrence, and an order that matches the order
/// the places first fired — that shape is what lets the record be left on in a process that
/// runs for a month, and a change that quietly made it one entry per acquisition would pass
/// a test that only asked whether a site appears.
/// </para>
/// </remarks>
public sealed class AmbientAuthorityAuditTests
{
    /// <summary>Authority taken while the recording is off leaves no trace.</summary>
    /// <remarks>
    /// The default, and the reason the default is safe to ship: a process that never asks
    /// for the record accumulates nothing, whatever it acquires.
    /// </remarks>
    [Fact]
    public void Nothing_is_recorded_while_the_recording_is_off()
    {
        if (AmbientAuthority.IsRecording)
        {
            Assert.Skip("The recording is forced on for this whole run, so it cannot be observed off.");
        }

        AmbientAuthority.Acquire();

        Assert.DoesNotContain(
            AmbientAuthority.RecordedSites,
            site => site.Member == nameof(Nothing_is_recorded_while_the_recording_is_off));
    }

    /// <summary>With it on, the place that took authority is named.</summary>
    [Fact]
    public void An_acquisition_is_recorded_against_its_call_site()
    {
        AmbientAuthoritySite site = Recording(static () =>
        {
            AmbientAuthority.Acquire();
            return SiteRecordedFor(nameof(An_acquisition_is_recorded_against_its_call_site));
        });

        Assert.Equal(nameof(An_acquisition_is_recorded_against_its_call_site), site.Member);
        Assert.EndsWith(nameof(AmbientAuthorityAuditTests) + ".cs", site.File, StringComparison.Ordinal);
        Assert.True(site.Line > 0);
        Assert.Equal(1, site.Count);
    }

    /// <summary>
    /// A line that takes authority repeatedly is one place, with a count.
    /// </summary>
    /// <remarks>
    /// The property that keeps the record bounded. A server taking authority per request
    /// would otherwise accumulate an entry per request, which would make the diagnostic a
    /// leak and so make it something nobody dares leave on — and the answer it gives would
    /// get harder to read the longer the process ran, rather than settling.
    /// </remarks>
    [Fact]
    public void Repeated_acquisitions_from_one_line_are_one_site_with_a_count()
    {
        AmbientAuthoritySite site = Recording(static () =>
        {
            for (int repeat = 0; repeat < 5; repeat++)
            {
                AmbientAuthority.Acquire();
            }

            return SiteRecordedFor(nameof(Repeated_acquisitions_from_one_line_are_one_site_with_a_count));
        });

        Assert.Equal(5, site.Count);
    }

    /// <summary>Two places are two entries, oldest first.</summary>
    /// <remarks>
    /// Order by first acquisition rather than by name or by count, because the question
    /// being asked of a start-up dump is what the process reached for and in what sequence.
    /// </remarks>
    [Fact]
    public void Sites_are_reported_in_the_order_they_first_took_authority()
    {
        List<AmbientAuthoritySite> recorded = Recording<List<AmbientAuthoritySite>>(static () =>
        {
            AmbientAuthority.Acquire();
            TakeAuthorityFromAnotherMember();

            return [.. AmbientAuthority.RecordedSites.Where(site =>
                site.Member is nameof(Sites_are_reported_in_the_order_they_first_took_authority)
                            or nameof(TakeAuthorityFromAnotherMember))];
        });

        Assert.Equal(
            [nameof(Sites_are_reported_in_the_order_they_first_took_authority), nameof(TakeAuthorityFromAnotherMember)],
            recorded.Select(site => site.Member));

        Assert.True(recorded[0].FirstAcquired <= recorded[1].FirstAcquired);
    }

    /// <summary>
    /// The time a place is stamped with is measured from the first acquisition, not from a
    /// clock.
    /// </summary>
    /// <remarks>
    /// The first line of a dump reads as zero, and nothing in the record discloses when the
    /// process ran. Reading a wall clock to say how far into start-up something happened
    /// would have this library consult ambient authority in order to report on ambient
    /// authority.
    /// </remarks>
    [Fact]
    public void The_first_recorded_site_is_stamped_at_zero()
    {
        IReadOnlyList<AmbientAuthoritySite> recorded = Recording(static () =>
        {
            AmbientAuthority.Acquire();
            return AmbientAuthority.RecordedSites;
        });

        Assert.NotEmpty(recorded);
        Assert.Equal(TimeSpan.Zero, recorded[0].FirstAcquired);
        Assert.All(recorded, site => Assert.True(site.FirstAcquired >= TimeSpan.Zero));
    }

    /// <summary>The report names every place it holds.</summary>
    [Fact]
    public void The_report_lists_the_recorded_sites()
    {
        string report = Recording(static () =>
        {
            AmbientAuthority.Acquire();
            TakeAuthorityElsewhere();

            return AmbientAuthority.DescribeRecordedSites();
        });

        Assert.Contains(nameof(The_report_lists_the_recorded_sites), report, StringComparison.Ordinal);
        Assert.Contains(nameof(TakeAuthorityElsewhere), report, StringComparison.Ordinal);
        Assert.Contains("Ambient authority was taken at", report, StringComparison.Ordinal);
    }

    /// <summary>A place that fired once says so; one that fired often says how often.</summary>
    [Fact]
    public void The_report_distinguishes_one_acquisition_from_many()
    {
        string report = Recording(static () =>
        {
            for (int repeat = 0; repeat < 3; repeat++)
            {
                AmbientAuthority.Acquire();
            }

            AmbientAuthoritySite site =
                SiteRecordedFor(nameof(The_report_distinguishes_one_acquisition_from_many));

            return site.ToString();
        });

        Assert.Contains("taken 3 times, first at", report, StringComparison.Ordinal);
        Assert.Contains(nameof(The_report_distinguishes_one_acquisition_from_many), report, StringComparison.Ordinal);
        Assert.DoesNotContain("taken once", report, StringComparison.Ordinal);
    }

    /// <summary>Turning the recording off stops new entries and keeps the old ones.</summary>
    /// <remarks>
    /// An audit a component could erase behind itself would answer a weaker question than
    /// the one being asked, so there is no way to clear the record — turning the switch off
    /// is as close as it gets, and it is not close.
    /// </remarks>
    [Fact]
    public void What_was_recorded_survives_the_recording_being_turned_off()
    {
        Recording(static () =>
        {
            AmbientAuthority.Acquire();
            return 0;
        });

        if (AmbientAuthority.IsRecording)
        {
            Assert.Skip("The recording is forced on for this whole run, so it cannot be turned off.");
        }

        // Off again, and this second acquisition is not recorded -- but the first still is.
        AmbientAuthority.Acquire();

        AmbientAuthoritySite site =
            SiteRecordedFor(nameof(What_was_recorded_survives_the_recording_being_turned_off));

        Assert.Equal(1, site.Count);
    }

    /// <summary>The switch is what the recording reports itself as following.</summary>
    [Fact]
    public void The_switch_decides_whether_an_acquisition_is_recorded()
    {
        if (AmbientAuthority.IsRecording)
        {
            Assert.Skip("The recording is forced on for this whole run, so the switch cannot be observed off.");
        }

        Assert.True(Recording(static () => AmbientAuthority.IsRecording));
        Assert.False(AmbientAuthority.IsRecording);
    }

    /// <summary>A second place to take authority from, so that two can be told apart.</summary>
    private static void TakeAuthorityElsewhere() => AmbientAuthority.Acquire();

    /// <summary>
    /// A third, used by the ordering test alone.
    /// </summary>
    /// <remarks>
    /// Places are ordered by when they <em>first</em> took authority, for the life of the
    /// process rather than of a test, so a helper shared with another test would already be
    /// on the record by the time the ordering test called it and would be reported ahead of
    /// a site that genuinely came later.
    /// </remarks>
    private static void TakeAuthorityFromAnotherMember() => AmbientAuthority.Acquire();

    /// <summary>The single recorded entry for a member, failing if there is not exactly one.</summary>
    private static AmbientAuthoritySite SiteRecordedFor(string member) =>
        Assert.Single(AmbientAuthority.RecordedSites, site => site.Member == member);

    /// <summary>
    /// Runs a body with the recording on, leaving the switch as it was found.
    /// </summary>
    /// <remarks>
    /// The switch is process-wide, which is why every test in this class asserts about its
    /// own call sites rather than about the whole record: another test acquiring on another
    /// thread while this one has the recording on is recorded too, and is none of its
    /// business.
    /// </remarks>
    private static T Recording<T>(Func<T> body)
    {
        bool previous =
            AppContext.TryGetSwitch(AmbientAuthority.RecordingSwitchName, out bool wasSet) && wasSet;
        AppContext.SetSwitch(AmbientAuthority.RecordingSwitchName, true);

        try
        {
            return body();
        }
        finally
        {
            AppContext.SetSwitch(AmbientAuthority.RecordingSwitchName, previous);
        }
    }
}
