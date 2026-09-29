using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// Which of the two native directory queries this process reads directories with.
/// </summary>
/// <remarks>
/// <para>
/// <c>NtQueryDirectoryFileEx</c> is an export of Windows 10 version 1709 and later, and the
/// versions before it that .NET still runs on — Windows Server 2016, the 1607 long-term
/// release — have only <c>NtQueryDirectoryFile</c>. An import binds when it is first called,
/// so a process on one of those would open a directory reader without complaint and fail on
/// its first read with an exception no caller translates. The extended call is used where it
/// is exported and the older one everywhere else; for everything this library asks of a
/// directory read the two are the same call.
/// </para>
/// <para>
/// Asked once, of the loaded module, rather than by calling the extended form and catching
/// its failure to bind: the answer does not change for the life of the process, and the
/// question costs nothing where a failed bind costs an exception.
/// </para>
/// </remarks>
internal static class DirectoryQueryEntryPoint
{
    /// <summary>The module both queries are exported from.</summary>
    internal const string LibraryName = "ntdll.dll";

    /// <summary>The export only Windows 10 version 1709 and later have.</summary>
    internal const string ExtendedName = "NtQueryDirectoryFileEx";

    /// <summary>
    /// True when this process reads directories with the extended query. Probed on first use.
    /// </summary>
    /// <remarks>Only ever read on Windows, where the module is already loaded.</remarks>
    internal static bool UsesExtended { get; } = Choose(IsExported);

    /// <summary>
    /// Chooses the query from whether the extended one is exported.
    /// </summary>
    /// <param name="isExported">Answers whether the named export exists in the module.</param>
    internal static bool Choose(Func<string, bool> isExported) => isExported(ExtendedName);

    /// <summary>Whether the module exports a function of this name.</summary>
    /// <remarks>
    /// The load only finds the module every Windows process already has mapped, so there is
    /// nothing to free afterwards that would ever be unloaded.
    /// </remarks>
    internal static bool IsExported(string name) =>
        NativeLibrary.TryLoad(LibraryName, out nint library) &&
        NativeLibrary.TryGetExport(library, name, out _);
}
