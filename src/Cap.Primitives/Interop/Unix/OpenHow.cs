using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The <c>open_how</c> argument to <c>openat2</c>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel is passed both this structure and its size, and rejects a size it does not
/// recognise. That is a deliberate extensibility mechanism — a newer kernel can grow the
/// structure and still serve an older caller — but it turns a layout mistake here into a
/// silent, permanent downgrade: the kernel answers "invalid argument", a capability probe
/// reads that as "this kernel does not have the syscall", caches the answer, and every
/// resolution for the life of the process takes the slower path with the weaker guarantee.
/// Nothing fails. No test goes red. The fast path simply never runs again.
/// </para>
/// <para>
/// Hence the layout assertions in the test suite, and hence the separate check that the
/// syscall is genuinely reached on a kernel that supports it. A test that only asserts
/// "resolution produced the right handle" passes just as happily on the fallback.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct OpenHow
{
    /// <summary>The <c>O_*</c> flags, as a 64-bit value rather than the <c>int</c> <c>openat</c> takes.</summary>
    public ulong Flags;

    /// <summary>The creation mode. Must be zero unless the flags ask for creation.</summary>
    public ulong Mode;

    /// <summary>The <c>RESOLVE_*</c> flags, which is what makes this syscall worth using.</summary>
    public ulong Resolve;

    /// <summary>The size the kernel is told the structure is.</summary>
    /// <remarks>
    /// Taken from the type rather than written as a literal, so that a field added here in
    /// error changes the size the kernel sees and is caught by the layout test instead of
    /// being passed off as a differently-shaped structure of the expected size.
    /// </remarks>
    public static unsafe nuint Size => (nuint)sizeof(OpenHow);
}

/// <summary>
/// The <c>RESOLVE_*</c> flags that confine an <c>openat2</c> resolution.
/// </summary>
[Flags]
internal enum ResolveFlags : ulong
{
    /// <summary>Unconfined: ordinary resolution.</summary>
    None = 0,

    /// <summary>Refuse to cross a mount point during resolution.</summary>
    NoCrossDevice = 0x01,

    /// <summary>
    /// Refuse to traverse a magic link — the entries under <c>/proc</c> that are not really
    /// symbolic links but jump directly to an open file, a process root, or a namespace.
    /// Never optional here: following one is a jump to somewhere the sandbox root has no
    /// relationship to, and it is a published escape route out of directory sandboxes.
    /// </summary>
    NoMagicLinks = 0x02,

    /// <summary>Refuse to traverse any symbolic link at all.</summary>
    NoSymlinks = 0x04,

    /// <summary>
    /// Confine resolution to the subtree below the directory it started from. Refuses a
    /// path that escapes, rather than clamping it — so an escape attempt is reported, not
    /// quietly rewritten into something that happens to stay inside.
    /// </summary>
    Beneath = 0x08,

    /// <summary>Treat the starting directory as the root, clamping <c>..</c> and absolute paths to it.</summary>
    InRoot = 0x10,
}
