using System.IO;
using System.Runtime.InteropServices;

namespace RAMChecker.Services;

// Обёртка над драйвером PawnIO: загружает модуль шины SMBus и вызывает его функции.
public sealed partial class PawnIO : IDisposable
{
    private const string Lib = "PawnIOLib";
    private readonly IntPtr handle;

    // Библиотека драйвера лежит в папке установки PawnIO, а не рядом с программой.
    static PawnIO() => NativeLibrary.SetDllImportResolver(typeof(PawnIO).Assembly, (name, _, _) => name == Lib
        ? NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"))
        : IntPtr.Zero);

    public PawnIO(string module)
    {
        Check(pawnio_open(out handle));
        try
        {
            using var stream = typeof(PawnIO).Assembly.GetManifestResourceStream("RAMChecker.Drivers." + module)!;
            var blob = new byte[stream.Length];
            stream.ReadExactly(blob);
            Check(pawnio_load(handle, blob, (nuint)blob.Length));
        }
        catch
        {
            pawnio_close(handle);
            throw;
        }
    }

    public bool Execute(string function, long[] input, long[] output) =>
        pawnio_execute(handle, function, input, (nuint)input.Length, output, (nuint)output.Length, out _) == 0;

    public void Dispose() => pawnio_close(handle);

    private static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    // [Out] обязателен: по правилам маршалинга массив передаётся только «внутрь», и без атрибута
    // результат функции драйвера официально не гарантированно возвращается в программу.
    [LibraryImport(Lib)]
    private static partial int pawnio_open(out IntPtr handle);
    [LibraryImport(Lib)]
    private static partial int pawnio_load(IntPtr handle, byte[] blob, nuint size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int pawnio_execute(IntPtr handle, string name, long[] input, nuint inSize, [Out] long[] output, nuint outSize, out nuint returned);
    [LibraryImport(Lib)]
    private static partial int pawnio_close(IntPtr handle);
}
