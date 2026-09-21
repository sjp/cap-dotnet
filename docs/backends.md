# Resolution backends and how to select them

cap-dotnet resolves a path against a `Dir` handle through one of three backends. Which one
runs changes the security properties — specifically, whether the residual TOCTOU window of
[threat model §6.1](threat-model.md#61-the-fallback-resolver-narrows-toctou-it-does-not-close-it)
exists at all — so it is observable, selectable, and tested in CI rather than being an
implementation detail.

| Backend | Platform | Issue | TOCTOU |
|---|---|---|---|
| `openat2(RESOLVE_BENEATH)` | Linux ≥ 5.6, when not blocked by seccomp | [0006](../issues/0006-linux-openat2.md) | none — resolution is kernel-atomic |
| Component-by-component walk | Linux fallback, macOS | [0007](../issues/0007-portable-component-walk.md) | narrowed, not eliminated |
| `NtCreateFile` with `RootDirectory` | Windows | [0008](../issues/0008-windows-resolution.md) | narrowed, not eliminated |

## Forcing the fallback

`openat2` is not always available: it needs Linux 5.6, and a seccomp filter can make it
return `EPERM` on a kernel that has it (Docker's default profile did exactly this for some
time after the syscall was added). The fallback is therefore not a legacy path — it is the
path a significant share of real deployments take, and it must be exercised as a first-class
configuration.

Two knobs select it, checked in this order:

1. **AppContext switch** `Cap.Primitives.DisableOpenat2`

   ```xml
   <!-- in the consuming application's .csproj -->
   <ItemGroup>
     <RuntimeHostConfigurationOption Include="Cap.Primitives.DisableOpenat2" Value="true" />
   </ItemGroup>
   ```

   or, before the first `Dir` is opened:

   ```csharp
   AppContext.SetSwitch("Cap.Primitives.DisableOpenat2", true);
   ```

2. **Environment variable** `CAPDOTNET_DISABLE_OPENAT2=1`

   This exists mainly so CI can set it per job, and so an operator can turn `openat2` off
   without a rebuild if a kernel regresses.

A switch, not a `#if`. The binary that is tested with the fallback forced is bit-identical
to the one users ship, which is the only way the fallback leg in CI means anything.

## Observability

Silently taking a weaker backend is, per [SECURITY.md](../SECURITY.md), a vulnerability in
its own right. So:

- The capability probe runs once at startup and caches its result, including **both**
  `ENOSYS` (kernel too old) and `EPERM` (seccomp). Retrying per call would be a syscall
  storm in exactly the deployments that can least afford it.
- The selected backend is reportable at runtime, and the CI fallback leg asserts the
  `openat2` call count is zero. A forced-fallback job that quietly keeps using `openat2`
  tests nothing at all.

> **TODO(0006):** name the public accessor for the active backend and the call counter here
> once they exist.

## A note on struct layout

`openat2` takes a pointer to `struct open_how` plus its size. If our declared size does not
match the kernel's, the syscall returns `EINVAL` — which the probe would read as "not
available", demoting every caller to the fallback permanently and silently. The fast path
would then never run again, on any machine, and every test would still pass.

This is why [0005](../issues/0005-interop-shim-layer.md) requires per-architecture struct
layout tests, and why the arm64 leg exists in CI.
