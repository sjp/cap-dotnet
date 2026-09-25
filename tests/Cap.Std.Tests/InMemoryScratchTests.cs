using Cap.Primitives;
using Cap.Std.Testing;

namespace Cap.Std.Tests;

/// <summary>
/// The in-memory filesystem's answer to where temporary files go, which only the library's
/// own tests can ask for, since the public way to ask is on the host.
/// </summary>
public sealed class InMemoryScratchTests
{
    [Fact]
    public void A_scratch_directory_through_the_backend_stays_out_of_the_tree()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("mine.txt", "x");

        using (CapTempDir temp = CapTempDir.NewThrough(fs.Backend, AmbientAuthority.Acquire()))
        {
            temp.Directory.WriteAllBytes("scratch.bin", [1]);
            Assert.Equal(ResolutionBackend.InMemory, temp.Directory.Backend);
            Assert.Equal([1], temp.Directory.ReadAllBytes("scratch.bin"));
        }

        Assert.Equal(["mine.txt"], fs.GetEntries());
        Assert.Equal(1, fs.UsedBytes);
    }
}
