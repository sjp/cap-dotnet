using Wasmtime;

namespace WasiHost.Preview1;

/// <summary>
/// Runs a WASI command module to completion.
/// </summary>
public static class WasiProgram
{
    /// <summary>
    /// Instantiates <paramref name="module"/> against a fresh adapter, calls its <c>_start</c>,
    /// and returns the exit code: the one it passed to <c>proc_exit</c>, or 0 if it returned.
    /// </summary>
    /// <exception cref="TrapException">
    /// The guest trapped — a Rust panic, for instance, ends in one — and so has no exit code.
    /// </exception>
    public static int Run(Engine engine, Module module, WasiOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(module);

        using WasiPreview1 wasi = new(options);
        using Linker linker = new(engine);
        using Store store = new(engine);
        wasi.DefineImports(linker);

        Instance instance = linker.Instantiate(store, module);
        Action start = instance.GetAction("_start")
            ?? throw new ArgumentException("The module exports no '_start' function.", nameof(module));

        try
        {
            start();
            return 0;
        }
        catch (WasmtimeException e) when (e.InnerException is WasiExitException exit)
        {
            return exit.Code;
        }
    }
}
