using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The second defence against Windows device names: what an open handle turns out to be.
/// </summary>
/// <remarks>
/// <para>
/// Refusing the names is the first defence and it is a blocklist, which is a shape that ages
/// badly — the reserved set belongs to the operating system and has grown before. A blocklist
/// that falls behind fails open, and what it would be failing open on is a handle to the
/// console or a serial port. So the object that was actually opened is asked what it is, and
/// anything that is not a file or directory on a filesystem is dropped.
/// </para>
/// <para>
/// Which is why these cases aim at the check directly rather than reaching it through a path.
/// Every route into it through ordinary resolution has the name refused first, so a test
/// written that way would assert nothing about this defence — it would only ever observe the
/// parser. The handles here are therefore opened by the test, using the very API whose name
/// rewriting the rest of the library avoids, and handed over as the fact they are.
/// </para>
/// <para>
/// All of it is the platform's own behaviour, so none of it can be asserted anywhere but on
/// the platform itself.
/// </para>
/// </remarks>
public sealed class WindowsDeviceHandleTests
{
    /// <summary>
    /// The device names to aim at the check. <c>NUL</c> leads because it is the one every
    /// Windows host has and will open for anybody, which makes it the case that holds the
    /// rest of this file to account: if nothing else here can be opened, that one still can.
    /// </summary>
    public static TheoryData<string> DeviceNames() =>
        ["NUL", "CON", "CONIN$", "CONOUT$", "COM1", "LPT1", "AUX", "PRN"];

    /// <summary>
    /// A handle to a device is refused, and refused as a device rather than as something
    /// that merely failed.
    /// </summary>
    /// <remarks>
    /// Not every one of these names reaches a device on every host — a machine with no serial
    /// port has nothing behind <c>COM1</c> — so a name the system will not open is passed
    /// over. <c>NUL</c> is not, because a host on which it cannot be opened is a broken
    /// harness rather than a configuration, and without that floor the whole theory could
    /// stand aside in silence.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeviceNames))]
    public void A_handle_to_a_device_is_refused(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using SafeFileHandle? device = TryOpenDevice(name);
        if (device is null)
        {
            Assert.NotEqual("NUL", name);
            Assert.Skip($"This host has no device behind '{name}'.");
        }

        CapError error = WindowsPlatformOps.RefuseUnlessFilesystemObject(device);

        Assert.True(error.IsFailure, $"a handle to '{name}' was accepted as a filesystem object.");
        Assert.Equal(CapErrorCategory.DeviceObject, error.Category);
    }

    /// <summary>
    /// An ordinary file and an ordinary directory are accepted, which is what makes the
    /// refusals above refusals of devices rather than of everything.
    /// </summary>
    [Fact]
    public void A_handle_to_a_file_or_a_directory_is_accepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("cap-dev-").FullName;
        try
        {
            string file = Path.Join(root, "ordinary.txt");
            File.WriteAllText(file, "x");

            using SafeFileHandle fileHandle = File.OpenHandle(file);
            Assert.True(
                WindowsPlatformOps.RefuseUnlessFilesystemObject(fileHandle).IsSuccess,
                "an ordinary file was refused as though it were a device.");

            CapResult<SafeDirHandle> directory = PlatformOps.Host.OpenAmbientDirectory(root, CapAccess.Read);
            Assert.True(directory.IsSuccess, directory.Error.FailureDescription);

            using SafeDirHandle handle = directory.Value!;
            Assert.True(
                WindowsPlatformOps.RefuseUnlessFilesystemObject(handle).IsSuccess,
                "an ordinary directory was refused as though it were a device.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A device name handed straight to the backend as a component, with the path parser out
    /// of the picture entirely, does not produce a handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape the published escape took, and the reason it worked there was that
    /// the name was resolved as a path. Resolved as a component against an open directory
    /// handle it cannot reach a device at all: the name goes to the filesystem as a counted
    /// string with that directory as the resolution root, and devices do not live in
    /// directories. The expected answer is therefore that no such entry exists.
    /// </para>
    /// <para>
    /// Asserted anyway, and asserted as "no handle" rather than as a particular error,
    /// because the value is in the absence of a handle and not in which layer produced the
    /// refusal. If some future change makes a component reach a device, this fails whether
    /// the check catches it or not.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeviceNames))]
    public void A_device_name_as_a_component_does_not_reach_a_device(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Directory.CreateTempSubdirectory("cap-dev-").FullName;
        try
        {
            CapResult<SafeDirHandle> opened = PlatformOps.Host.OpenAmbientDirectory(root, CapAccess.Read);
            Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
            using SafeDirHandle directory = opened.Value!;

            CapResult<SafeFileHandle> file =
                PlatformOps.Host.OpenChildFile(directory, name, FileOpenRequest.Existing(FileAccess.Read));
            if (file.IsSuccess)
            {
                file.Value!.Dispose();
                Assert.Fail($"'{name}' was opened as a file beneath a directory handle.");
            }

            CapResult<SafeDirHandle> child =
                PlatformOps.Host.OpenChildDirectory(directory, name, CapAccess.Read);
            if (child.IsSuccess)
            {
                child.Value!.Dispose();
                Assert.Fail($"'{name}' was opened as a directory beneath a directory handle.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Opens a device by name through the API that resolves one, or reports that this host
    /// has nothing behind the name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the Win32 call, and deliberately a bare name. That is precisely the
    /// combination the library never uses and the one that reaches a device from anywhere: the
    /// name is mapped by the object manager rather than looked up in a directory. Nothing else
    /// produces the handle this file needs.
    /// </para>
    /// <para>
    /// Three access masks are tried because devices disagree about what they will grant. A
    /// console is readable or writable depending on which half of it the name refers to, and a
    /// request for no access at all is enough to obtain a handle to interrogate, which is all
    /// that is wanted here.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? TryOpenDevice(string name)
    {
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;

        foreach (uint access in (uint[])[0, GenericRead, GenericWrite])
        {
            nint raw = NtNative.CreateFile(
                name,
                access,
                NtConstants.FILE_SHARE_ALL,
                securityAttributes: 0,
                NtConstants.OPEN_EXISTING,
                flagsAndAttributes: 0,
                templateFile: 0);

            if (raw != NtConstants.INVALID_HANDLE_VALUE)
            {
                return new SafeFileHandle(raw, ownsHandle: true);
            }
        }

        return null;
    }
}
