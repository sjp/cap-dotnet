using System.Runtime.CompilerServices;

// Every call into the operating system goes through a [LibraryImport] stub that the source
// generator writes at compile time, and those stubs pass only blittable values. This makes
// that a property the compiler and runtime enforce rather than a habit: a declaration that
// would need the runtime to marshal a non-blittable type for it no longer compiles, and
// nothing in this assembly can fall back on marshalling code generated at run time, which a
// NativeAOT or trimmed application may not have.
[assembly: DisableRuntimeMarshalling]
