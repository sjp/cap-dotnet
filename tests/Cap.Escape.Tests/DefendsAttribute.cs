namespace Cap.Escape.Tests;

/// <summary>
/// Names the rows of the threat model a test outside the case table defends.
/// </summary>
/// <remarks>
/// Most attacks are rows of the case table, which records its own rows. A few need a tree the
/// table cannot describe — a mount, a name only the volume knows, a second root — and are
/// written as tests of their own; this is how they are counted when the suite checks that
/// every row of the threat model has something defending it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class DefendsAttribute(params string[] rows) : Attribute
{
    /// <summary>The threat-model rows, such as <c>T1</c> or <c>W6</c>.</summary>
    public string[] Rows { get; } = rows;
}
