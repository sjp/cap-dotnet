using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Cap.Rand;
using Cap.Std;
using Microsoft.Extensions.Time.Testing;
using Wasmtime;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>
/// A WebAssembly guest that exists only to make WASI calls on a test's behalf.
/// </summary>
/// <remarks>
/// <para>
/// Each filesystem import is re-exported through a function of the guest's own that calls it,
/// so a test can make one WASI call at a time with arguments it chose, and the call still
/// crosses the engine exactly as a real guest's would: the arguments are guest integers, the
/// path is bytes in guest memory, and the adapter reads it from there. Nothing reaches the
/// adapter except through the ABI.
/// </para>
/// <para>
/// The directory handed in is preopened as descriptor 3, the only authority the guest has.
/// </para>
/// </remarks>
internal sealed class TrampolineGuest : IDisposable
{
    /// <summary>The preopened directory's descriptor.</summary>
    public const int Root = 3;

    /// <summary>Where the first path argument is written.</summary>
    public const int PathSlot = 0x10000;

    /// <summary>Where the second path argument is written.</summary>
    public const int SecondPathSlot = 0x20000;

    /// <summary>Where a call writes a descriptor, a count or a record it returns.</summary>
    public const int ResultSlot = 0x100;

    /// <summary>A single I/O vector, pointing at <see cref="DataSlot"/>.</summary>
    public const int IoVecSlot = 0x200;

    /// <summary>A buffer for data read or written.</summary>
    public const int DataSlot = 0x1000;

    /// <summary>The size of <see cref="DataSlot"/>.</summary>
    public const int DataLength = 0x4000;

    /// <summary>
    /// The largest path, in bytes, a slot holds: room for the longest path the library
    /// accepts, written entirely in characters that take three bytes, and more.
    /// </summary>
    private const int PathCapacity = 0x10000;

    /// <summary>The imports the guest re-exports, with one letter per parameter.</summary>
    private static readonly (string Name, string Signature)[] Imports =
    [
        ("fd_close", "i"),
        ("fd_filestat_get", "ii"),
        ("fd_read", "iiii"),
        ("fd_readdir", "iiiIi"),
        ("fd_write", "iiii"),
        ("path_create_directory", "iii"),
        ("path_filestat_get", "iiiii"),
        ("path_filestat_set_times", "iiiiIIi"),
        ("path_link", "iiiiiii"),
        ("path_open", "iiiiiIIii"),
        ("path_readlink", "iiiiii"),
        ("path_remove_directory", "iii"),
        ("path_rename", "iiiiii"),
        ("path_symlink", "iiiii"),
        ("path_unlink_file", "iii"),
    ];

    private static readonly Lazy<(Engine Engine, Module Module)> Compiled = new(() =>
    {
        Engine engine = new();
        return (engine, Module.FromText(engine, "trampoline", BuildText()));
    });

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly WasiPreview1 _wasi;
    private readonly Linker _linker;
    private readonly Store _store;
    private readonly Instance _instance;
    private readonly Memory _memory;

    public TrampolineGuest(Dir root)
    {
        (Engine engine, Module module) = Compiled.Value;

        _wasi = new WasiPreview1(new WasiOptions
        {
            Clock = new FakeTimeProvider(),
            Random = new InsecureDeterministicRandom(0),
            Preopens = [("/", root)],
        });
        _linker = new Linker(engine);
        _store = new Store(engine);
        _wasi.DefineImports(_linker);
        _instance = _linker.Instantiate(_store, module);
        _memory = _instance.GetMemory("memory")!;
    }

    /// <summary>
    /// Whether a path can be carried across the ABI at all. WASI paths are UTF-8, and a string
    /// holding an unpaired surrogate has no UTF-8 spelling.
    /// </summary>
    public static bool CanCarry(string path)
    {
        try
        {
            int length = StrictUtf8.GetByteCount(path);
            return length <= PathCapacity
                ? true
                : throw new ArgumentException($"A {length}-byte path does not fit in the guest's path slot.", nameof(path));
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Makes one WASI call through the guest.</summary>
    public Errno Call(string name, params ValueBox[] arguments) =>
        (Errno)(int)_instance.GetFunction(name)!.Invoke(arguments)!;

    /// <summary>Writes a path into guest memory and returns its length in bytes.</summary>
    public int WritePath(int slot, string path)
    {
        byte[] bytes = StrictUtf8.GetBytes(path);
        bytes.CopyTo(_memory.GetSpan(slot, bytes.Length));
        return bytes.Length;
    }

    public uint ReadU32(int address) => BinaryPrimitives.ReadUInt32LittleEndian(_memory.GetSpan(address, 4));

    public ulong ReadU64(int address) => BinaryPrimitives.ReadUInt64LittleEndian(_memory.GetSpan(address, 8));

    public Span<byte> Bytes(int address, int length) => _memory.GetSpan(address, length);

    /// <summary>Points the I/O vector at the data buffer, <paramref name="length"/> bytes long.</summary>
    public void SetIoVec(int length)
    {
        Span<byte> vector = _memory.GetSpan(IoVecSlot, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(vector, DataSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(vector[4..], (uint)length);
    }

    public void Dispose()
    {
        _wasi.Dispose();
        _store.Dispose();
        _linker.Dispose();
    }

    /// <summary>
    /// The guest's text: each import, and an export of the same name that passes its
    /// arguments straight through.
    /// </summary>
    private static string BuildText()
    {
        StringBuilder text = new("(module\n");
        foreach ((string name, string signature) in Imports)
        {
            string parameters = string.Join(' ', signature.Select(Kind));
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"  (import \"{WasiPreview1.ModuleName}\" \"{name}\" (func ${name} (param {parameters}) (result i32)))");
        }

        // Three pages of 64 KiB: the small slots and the data buffer, then one per path.
        text.AppendLine("  (memory (export \"memory\") 3)");
        foreach ((string name, string signature) in Imports)
        {
            string parameters = string.Join(' ', signature.Select(Kind));
            string forward = string.Join(' ', Enumerable.Range(0, signature.Length).Select(i => $"(local.get {i})"));
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"  (func (export \"{name}\") (param {parameters}) (result i32) (call ${name} {forward}))");
        }

        return text.Append(')').ToString();

        static string Kind(char letter) => letter == 'I' ? "i64" : "i32";
    }
}
