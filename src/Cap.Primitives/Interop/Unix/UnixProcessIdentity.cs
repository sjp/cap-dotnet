using System.Runtime.Versioning;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Who this process is, as the filesystem sees it when it decides what the process may do.
/// </summary>
/// <remarks>
/// The effective id rather than the real one, because that is the id permission checks and
/// new objects' ownership are made against. The two differ only in a set-user-id program,
/// and there it is the effective id that decides whose files the process is handling.
/// </remarks>
internal static class UnixProcessIdentity
{
    /// <summary>The user id permission checks are made against.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static uint EffectiveUserId =>
        OperatingSystem.IsMacOS() ? DarwinNative.GetEffectiveUserId() : LinuxNative.GetEffectiveUserId();
}
