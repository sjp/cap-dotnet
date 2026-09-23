// One test at a time. The resolution target substitutes a simulated filesystem for the
// platform, and that substitution is process-wide: a second test running alongside would find
// its handles belonging to someone else's tree.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
