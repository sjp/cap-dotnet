namespace Cap.Std.Testing.Tests;

/// <summary>
/// One filesystem used from many threads, and many filesystems used at once.
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public async Task One_filesystem_takes_writes_from_many_threads()
    {
        InMemoryFileSystem fs = new();
        using (Dir root = fs.OpenRoot())
        {
            await Parallel.ForAsync(0, 64, TestContext.Current.CancellationToken, (i, cancellation) =>
            {
                using Dir own = root.CreateDir($"worker-{i}");
                for (int j = 0; j < 20; j++)
                {
                    own.WriteAllBytes($"file-{j}.bin", [(byte)i, (byte)j]);
                    _ = own.ReadAllBytes($"file-{j}.bin");
                    _ = root.EnumerateEntries().Count();
                }

                return ValueTask.CompletedTask;
            });
        }

        Assert.Equal(64, fs.GetEntries().Count);
        Assert.All(fs.GetEntries(), name => Assert.Equal(20, fs.GetEntries(name).Count));
        Assert.Equal(64 * 20 * 2, fs.UsedBytes);
        Assert.Equal(0, fs.OpenHandleCount);
    }

    [Fact]
    public async Task One_file_takes_appends_from_many_threads_without_losing_any()
    {
        InMemoryFileSystem fs = new();
        using Dir root = fs.OpenRoot();

        await Parallel.ForAsync(0, 32, TestContext.Current.CancellationToken, (_, _) =>
        {
            // Shared for writing, which every writer needs under Windows rules, as on Windows.
            using CapFile file = root.OpenFile("shared.log", FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            for (int j = 0; j < 50; j++)
            {
                file.Write([1, 2, 3, 4], 0);
            }

            return ValueTask.CompletedTask;
        });

        Assert.Equal(32 * 50 * 4, fs.ReadAllBytes("shared.log").Length);
    }

    [Fact]
    public async Task Readers_of_one_file_see_each_write_whole_while_it_is_rewritten()
    {
        const int Size = 4096;
        InMemoryFileSystem fs = new();
        fs.AddFile("shared.bin", new byte[Size]);
        using Dir root = fs.OpenRoot();
        using CapFile writer = root.OpenFile("shared.bin", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Every write fills the whole file with one value, so a read that sees two values has
        // seen part of one write and part of another.
        Task writing = Task.Run(
            () =>
            {
                try
                {
                    byte[] block = new byte[Size];
                    for (int round = 1; round <= 500; round++)
                    {
                        Array.Fill(block, (byte)round);
                        writer.Write(block, 0);
                    }
                }
                finally
                {
                    done.Cancel();
                }
            },
            TestContext.Current.CancellationToken);

        int reads = 0;
        await Parallel.ForAsync(0, 8, TestContext.Current.CancellationToken, (_, _) =>
        {
            using CapFile reader = root.OpenFile("shared.bin", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] seen = new byte[Size];
            do
            {
                Assert.Equal(Size, reader.Read(seen, 0));
                Assert.Equal(Size, reader.Length);
                Assert.True(seen.AsSpan().IndexOfAnyExcept(seen[0]) < 0, "A read saw two writes at once.");
                _ = Interlocked.Increment(ref reads);
            }
            while (!done.IsCancellationRequested);

            return ValueTask.CompletedTask;
        });

        await writing;
        Assert.True(reads >= 8);
        Assert.Equal(Enumerable.Repeat((byte)(500 % 256), Size), fs.ReadAllBytes("shared.bin"));
    }

    [Fact]
    public async Task Separate_filesystems_do_not_see_each_other()
    {
        InMemoryFileSystem[] filesystems = [.. Enumerable.Range(0, 16).Select(_ => new InMemoryFileSystem())];

        await Parallel.ForAsync(0, filesystems.Length, TestContext.Current.CancellationToken, (i, cancellation) =>
        {
            using Dir root = filesystems[i].OpenRoot();
            root.WriteAllBytes("mine.txt", [(byte)i]);
            return ValueTask.CompletedTask;
        });

        for (int i = 0; i < filesystems.Length; i++)
        {
            Assert.Equal(["mine.txt"], filesystems[i].GetEntries());
            Assert.Equal([(byte)i], filesystems[i].ReadAllBytes("mine.txt"));
        }
    }
}
