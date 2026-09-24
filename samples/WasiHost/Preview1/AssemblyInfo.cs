using Cap.Primitives;

// Every rule of the analyzer is an error here. The adapter reaches the filesystem only through
// the directories the host hands it, and the clock and entropy only through what the host
// hands it as well; a call that reached for any of them directly would not compile.
[assembly: CapabilityStrict]
