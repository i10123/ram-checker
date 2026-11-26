using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RAMChecker.Services;

// Обёртка над драйвером PawnIO: загружает модуль шины SMBus и вызывает его функции.
public sealed partial class PawnIO : IDisposable
{
    private const string Lib = "PawnIOLib";
    private static readonly object LibraryLock = new();
    private static IntPtr libraryHandle;
    private readonly PawnIoHandle handle;

    // Библиотека драйвера лежит в папке установки PawnIO, а не рядом с программой.
    static PawnIO() => NativeLibrary.SetDllImportResolver(typeof(PawnIO).Assembly, (name, _, _) => name == Lib
        ? LoadLibrary()
        : IntPtr.Zero);

    private static IntPtr LoadLibrary()
    {
        lock (LibraryLock)
        {
            if (libraryHandle != IntPtr.Zero)
                return libraryHandle;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!Path.IsPathFullyQualified(programFiles))
                throw new DllNotFoundException("Не удалось определить каталог установки PawnIO.");

            string libraryPath = Path.Combine(programFiles, "PawnIO", "PawnIOLib.dll");
            // LoadLibraryEx ограничивает поиск зависимостей каталогом PawnIO и System32.
            var loadedLibrary = LoadLibraryEx(libraryPath, IntPtr.Zero,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            if (loadedLibrary == IntPtr.Zero)
                throw new DllNotFoundException(libraryPath, new Win32Exception(Marshal.GetLastPInvokeError()));

            if (!NativeLibrary.TryGetExport(loadedLibrary, "pawnio_close", out _))
            {
                NativeLibrary.Free(loadedLibrary);
                throw new EntryPointNotFoundException("PawnIOLib.dll: pawnio_close");
            }

            libraryHandle = loadedLibrary;
            return libraryHandle;
        }
    }

    public PawnIO(string moduleFileName)
    {
        using var stream = typeof(PawnIO).Assembly.GetManifestResourceStream("RAMChecker.Drivers." + moduleFileName)
            ?? throw new ArgumentException("Неизвестный модуль PawnIO.", nameof(moduleFileName));
        var moduleBytes = new byte[stream.Length];
        stream.ReadExactly(moduleBytes);

        int openResult = pawnio_open(out handle);
        try
        {
            ThrowIfFailed(openResult);
            ThrowIfFailed(pawnio_load(handle, moduleBytes, (nuint)moduleBytes.Length));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool Execute(string function, long[] input, long[] output) =>
        Execute(function, input, output, out _);

    internal bool Execute(string function, long[] input, long[] output, out nuint returned) =>
        pawnio_execute(handle, function, input, (nuint)input.Length, output, (nuint)output.Length, out returned) == 0;

    public void Dispose() => handle.Dispose();

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    // [Out] обязателен: по правилам маршалинга массив передаётся только «внутрь», и без атрибута
    // результат функции драйвера официально не гарантированно возвращается в программу.
    [LibraryImport(Lib)]
    private static partial int pawnio_open(out PawnIoHandle handle);
    [LibraryImport(Lib)]
    private static partial int pawnio_load(PawnIoHandle handle, byte[] moduleBytes, nuint size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int pawnio_execute(PawnIoHandle handle, string name, long[] input, nuint inSize, [Out] long[] output, nuint outSize, out nuint returned);
    [LibraryImport(Lib)]
    private static partial int pawnio_close(IntPtr handle);
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryEx(string path, IntPtr file, DllImportSearchPath flags);

    private sealed class PawnIoHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PawnIoHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => pawnio_close(handle) >= 0;
    }
}
