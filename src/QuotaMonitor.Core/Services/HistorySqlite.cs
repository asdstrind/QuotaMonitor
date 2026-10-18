using System.Runtime.InteropServices;

namespace QuotaMonitor.Core.Services;

// Windows inbox SQLite. Production callers only use READONLY; no native dependency is downloaded.
internal sealed class HistorySqlite : IDisposable
{
    private IntPtr db;
    private readonly Progress progress;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress(IntPtr state);
    internal HistorySqlite(string path, Func<bool>? stop = null, bool immutable = false, bool writableFixture = false)
    {
        progress = _ => stop?.Invoke() == true ? 1 : 0;
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + (immutable ? "?immutable=1" : "");
        if (Open(uri, out db, (writableFixture ? 6 : 1) | 0x40 | 0x10000, IntPtr.Zero) != 0) { Dispose(); throw new InvalidDataException("SQLite open unavailable."); }
        Busy(db, 100);
        Limit(db, 0, 1_048_576 + 4096);
        ProgressHandler(db, 1000, progress, IntPtr.Zero);
    }
    internal IEnumerable<IntPtr> Rows(string sql)
    {
        if (Prepare(db, sql, -1, out var stmt, IntPtr.Zero) != 0) throw new InvalidDataException("Unsupported SQLite query.");
        try
        {
            int result;
            while ((result = Step(stmt)) == 100) yield return stmt;
            if (result != 101) throw new InvalidDataException("Incomplete SQLite snapshot.");
        }
        finally { Finalize(stmt); }
    }
    internal void Execute(string sql) { foreach (var _ in Rows(sql)) { } }
    internal static long Integer(IntPtr row, int col) => ColumnInt64(row, col);
    internal static int Type(IntPtr row, int col) => ColumnType(row, col);
    internal static string Text(IntPtr row, int col)
    {
        var count=ColumnBytes(row,col);if(count>65536)throw new InvalidDataException("Unsupported schema text size.");
        return Marshal.PtrToStringUTF8(ColumnText(row,col),count)??"";
    }
    internal static byte[] Blob(IntPtr row, int col, int max)
    {
        var size = ColumnBytes(row, col);
        if (ColumnType(row, col) != 4 || size < 0 || size > max) throw new InvalidDataException("Unsupported metadata size/type.");
        var data = new byte[size]; if (size > 0) Marshal.Copy(ColumnBlob(row, col), data, 0, size); return data;
    }
    public void Dispose() { if (db != IntPtr.Zero) { Close(db); db = IntPtr.Zero; } GC.KeepAlive(progress); }
    [DllImport("winsqlite3", EntryPoint="sqlite3_open_v2", CallingConvention=CallingConvention.Cdecl)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", EntryPoint="sqlite3_close_v2", CallingConvention=CallingConvention.Cdecl)] private static extern int Close(IntPtr db);
    [DllImport("winsqlite3", EntryPoint="sqlite3_prepare_v2", CallingConvention=CallingConvention.Cdecl)] private static extern int Prepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3", EntryPoint="sqlite3_step", CallingConvention=CallingConvention.Cdecl)] private static extern int Step(IntPtr stmt);
    [DllImport("winsqlite3", EntryPoint="sqlite3_finalize", CallingConvention=CallingConvention.Cdecl)] private static extern int Finalize(IntPtr stmt);
    [DllImport("winsqlite3", EntryPoint="sqlite3_column_int64", CallingConvention=CallingConvention.Cdecl)] private static extern long ColumnInt64(IntPtr stmt,int col);
    [DllImport("winsqlite3", EntryPoint="sqlite3_column_type", CallingConvention=CallingConvention.Cdecl)] private static extern int ColumnType(IntPtr stmt,int col);
    [DllImport("winsqlite3", EntryPoint="sqlite3_column_text", CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr ColumnText(IntPtr stmt,int col);
    [DllImport("winsqlite3", EntryPoint="sqlite3_column_blob", CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr ColumnBlob(IntPtr stmt,int col);
    [DllImport("winsqlite3", EntryPoint="sqlite3_column_bytes", CallingConvention=CallingConvention.Cdecl)] private static extern int ColumnBytes(IntPtr stmt,int col);
    [DllImport("winsqlite3", EntryPoint="sqlite3_busy_timeout", CallingConvention=CallingConvention.Cdecl)] private static extern int Busy(IntPtr db,int ms);
    [DllImport("winsqlite3", EntryPoint="sqlite3_limit", CallingConvention=CallingConvention.Cdecl)] private static extern int Limit(IntPtr db,int id,int value);
    [DllImport("winsqlite3", EntryPoint="sqlite3_progress_handler", CallingConvention=CallingConvention.Cdecl)] private static extern void ProgressHandler(IntPtr db,int instructions,Progress callback,IntPtr state);
}
