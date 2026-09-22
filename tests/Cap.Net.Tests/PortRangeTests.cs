namespace Cap.Net.Tests;

/// <summary>
/// The half of a range grant that names the service.
/// </summary>
/// <remarks>
/// Small enough to look obvious, and worth testing for one reason: the value nobody
/// constructed. A range whose default was the lowest port would make a grant written with a
/// forgotten argument silently mean something, and the something it meant would be a real
/// port.
/// </remarks>
public sealed class PortRangeTests
{
    /// <summary>The value nobody built contains nothing.</summary>
    [Fact]
    public void The_default_range_is_empty()
    {
        PortRange range = default;

        Assert.True(range.IsEmpty);
        Assert.False(range.Contains(0));
        Assert.False(range.Contains(1));
        Assert.False(range.Contains(443));
        Assert.Equal(-1, range.First);
        Assert.Equal(-1, range.Last);
    }

    /// <summary>A single port is that port and its neighbours are not.</summary>
    [Fact]
    public void A_single_port_range_contains_one_port()
    {
        PortRange range = PortRange.Only(443);

        Assert.True(range.Contains(443));
        Assert.False(range.Contains(442));
        Assert.False(range.Contains(444));
        Assert.Equal(443, range.First);
        Assert.Equal(443, range.Last);
    }

    /// <summary>Both ends of a written range are inside it.</summary>
    [Fact]
    public void A_range_includes_both_of_its_ends()
    {
        PortRange range = PortRange.Between(8000, 8002);

        Assert.True(range.Contains(8000));
        Assert.True(range.Contains(8001));
        Assert.True(range.Contains(8002));
        Assert.False(range.Contains(7999));
        Assert.False(range.Contains(8003));
    }

    /// <summary>Every port there is, and still not port zero.</summary>
    [Fact]
    public void Every_port_stops_short_of_the_one_that_is_not_a_port()
    {
        PortRange range = PortRange.Every;

        Assert.True(range.Contains(1));
        Assert.True(range.Contains(65535));
        Assert.False(range.Contains(0));
        Assert.False(range.Contains(65536));
    }

    /// <summary>A port outside the numbering, or a range running backwards, is refused.</summary>
    [Fact]
    public void A_range_that_cannot_mean_anything_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortRange.Only(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortRange.Only(65536));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortRange.Between(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortRange.Between(100, 99));
    }

    /// <summary>Two ranges covering the same ports are the same range.</summary>
    [Fact]
    public void Ranges_covering_the_same_ports_are_equal()
    {
        Assert.Equal(PortRange.Only(443), PortRange.Between(443, 443));
        Assert.NotEqual(PortRange.Only(443), PortRange.Only(444));
        Assert.True(PortRange.Only(443) == PortRange.Between(443, 443));
        Assert.True(PortRange.Only(443) != PortRange.None);
    }
}
