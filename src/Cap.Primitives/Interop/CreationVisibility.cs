namespace Cap.Primitives.Interop;

/// <summary>
/// How much of the rest of the machine may see into something this library creates.
/// </summary>
/// <remarks>
/// <para>
/// Almost everything created through a capability takes <see cref="SystemDefault"/>, and
/// that is not laziness. The caller chose where the object goes, the permissions it ends up
/// with are the ones any other program creating a file in that place would get, and a
/// library that quietly asked for something narrower would make objects created through a
/// handle differ from every other object on the system — a surprise, not a defence.
/// </para>
/// <para>
/// <see cref="OwnerOnly"/> exists for the one case where that reasoning does not hold:
/// where this library, rather than the caller, picked the place. A scratch directory made in
/// the system temporary location is put somewhere shared by every account on the machine,
/// nobody asked for it to be there, and so closing it to everybody else is part of putting
/// it there at all.
/// </para>
/// </remarks>
internal enum CreationVisibility : byte
{
    /// <summary>
    /// Whatever the platform gives an object created in that place: on Unix the usual
    /// creation mode narrowed by the process umask, on Windows the permissions the
    /// containing directory hands down.
    /// </summary>
    SystemDefault = 0,

    /// <summary>
    /// Reachable by the account that created it and nobody else, where the platform records
    /// permissions per object that way.
    /// </summary>
    /// <remarks>
    /// On Unix this is mode 0700, which is what <c>mkdtemp</c> asks for and for the same
    /// reason. On Windows it is not expressible as a creation flag — access there is decided
    /// by a security descriptor inherited from the containing directory — so this is the
    /// same as <see cref="SystemDefault"/>, and the protection comes from the temporary
    /// location itself being per-account.
    /// </remarks>
    OwnerOnly,
}
