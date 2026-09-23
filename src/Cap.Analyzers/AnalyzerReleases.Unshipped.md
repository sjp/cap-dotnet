; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CAP0001 | Capability | Disabled | Ambient filesystem access
CAP0002 | Capability | Disabled | Ambient network access
CAP0003 | Capability | Warning | Ambient authority taken outside a composition root
CAP0004 | Capability | Info | Raw handle taken from a capability
CAP0005 | Capability | Warning | Path built by concatenation
CAP0006 | Capability | Disabled | Ambient clock access
CAP0007 | Capability | Disabled | Ambient entropy
CAP0008 | Capability | Warning | Symbol banned by this project
