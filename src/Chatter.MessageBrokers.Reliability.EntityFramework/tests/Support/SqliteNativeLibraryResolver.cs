using SQLitePCL;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Lets the SQLite-backed tests run on Linux hosts whose glibc is older than the one the bundled
    /// e_sqlite3 native was linked against, by falling back to the host's system SQLite. The bundled
    /// native is always tried first, so hosts that can load it — including CI — resolve exactly as they
    /// would without this resolver.
    /// </summary>
    internal static class SqliteNativeLibraryResolver
    {
        private const string SqliteLibraryName = "e_sqlite3";
        private const string SystemSqliteLibraryPath = "/usr/lib/x86_64-linux-gnu/libsqlite3.so.0";

        [ModuleInitializer]
        internal static void RegisterResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_e_sqlite3).Assembly, ResolveSqliteLibrary);
        }

        private static IntPtr ResolveSqliteLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, SqliteLibraryName, StringComparison.Ordinal))
            {
                return IntPtr.Zero;
            }

            // INVARIANT: NativeLibrary.TryLoad resolves through the runtime's default probing and never
            // re-enters a registered DllImportResolver, so this call cannot recurse into this method.
            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var bundledHandle))
            {
                return bundledHandle;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !File.Exists(SystemSqliteLibraryPath))
            {
                return IntPtr.Zero;
            }

            return NativeLibrary.TryLoad(SystemSqliteLibraryPath, out var systemHandle) ? systemHandle : IntPtr.Zero;
        }
    }
}
