namespace Cap.Primitives;

/// <summary>
/// Marks the code that composes the application, where taking ambient authority is
/// expected.
/// </summary>
/// <remarks>
/// <para>
/// A program should reach past what it was given in one place: where it is assembled, which
/// opens the first directories, takes the clock and the entropy source, and hands them down
/// to components that then hold nothing else. The analyzer reports each call to
/// <see cref="AmbientAuthority.Acquire"/> made anywhere else, so that authority taken deep
/// inside a component shows up as a warning rather than as a line nobody searched for.
/// </para>
/// <para>
/// The program's entry point is a composition root without being marked. Beyond it, a type
/// or member carrying this attribute is one, and so is everything declared inside it. Whole
/// files can be designated instead from <c>.editorconfig</c>, which is the form that suits a
/// folder of start-up code:
/// </para>
/// <code>
/// [src/Startup/**.cs]
/// cap_composition_root = true
/// </code>
/// <para>
/// Marking something a composition root changes nothing at run time. The acquisition is
/// still recorded when recording is on, and still appears in a search for the method.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
    AttributeTargets.Constructor | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
public sealed class CompositionRootAttribute : Attribute
{
}
