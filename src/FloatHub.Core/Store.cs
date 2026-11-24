using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FloatHub.Core;

// Windows ships SQLite. Each value is replaced by a single atomic SQLite statement.
public sealed class Store : IDisposable
{
    private IntPtr db;
    private readonly object gate = new();
    public string Folder { get; }

    public Store(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        Check(Sql.sqlite3_open_v2(Path.Combine(folder, "floathub.db"), out db, 6, IntPtr.Zero));
        Sql.sqlite3_busy_timeout(db, 3000);
        Execute("CREATE TABLE IF NOT EXISTS state (id TEXT PRIMARY KEY, value TEXT NOT NULL)", _ => { });
    }

    public T Read<T>(string key, T fallback)
    {
        lock (gate)
        {
            var result = fallback;
            Execute("SELECT value FROM state WHERE id=?1", stmt =>
            {
                Bind(stmt, 1, key);
                var rc = Sql.sqlite3_step(stmt);
                if (rc == 101) return;
                if (rc != 100) Check(rc);
                var json = Marshal.PtrToStringUTF8(Sql.sqlite3_column_text(stmt, 0))!;
                result = JsonSerializer.Deserialize<T>(json) ?? fallback;
            }, false);
            return result;
        }
    }

    public void Write<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        lock (gate)
            Execute("INSERT OR REPLACE INTO state(id,value) VALUES(?1,?2)", stmt =>
            {
                Bind(stmt, 1, key);
                Bind(stmt, 2, json);
            });
    }

    public void Backup(string destination)
    {
        string target = Path.GetFullPath(destination);
        if (string.Equals(target, Path.GetFullPath(Path.Combine(Folder, "floathub.db")), StringComparison.OrdinalIgnoreCase))
            throw new IOException("请将备份保存到其他文件。");
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (gate)
            {
                int opened = Sql.sqlite3_open_v2(temp, out var copy, 6, IntPtr.Zero);
                try
                {
                    Check(opened);
                    var backup = Sql.sqlite3_backup_init(copy, "main", db, "main");
                    if (backup == IntPtr.Zero) throw new IOException("无法创建 SQLite 备份。");
                    int result;
                    try { result = Sql.sqlite3_backup_step(backup, -1); }
                    finally { Check(Sql.sqlite3_backup_finish(backup)); }
                    if (result != 101) Check(result);
                }
                finally { if (copy != IntPtr.Zero) Sql.sqlite3_close_v2(copy); }
            }
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void Execute(string sql, Action<IntPtr> configure, bool step = true)
    {
        Check(Sql.sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero));
        try
        {
            configure(stmt);
            if (step)
            {
                var rc = Sql.sqlite3_step(stmt);
                if (rc != 101) Check(rc);
            }
        }
        finally { Sql.sqlite3_finalize(stmt); }
    }

    private void Bind(IntPtr stmt, int index, string value) =>
        Check(Sql.sqlite3_bind_text(stmt, index, value, -1, new IntPtr(-1)));

    private void Check(int rc)
    {
        if (rc != 0) throw new IOException($"SQLite {rc} · {Marshal.PtrToStringUTF8(Sql.sqlite3_errmsg(db))}");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (db == IntPtr.Zero) return;
            Sql.sqlite3_close_v2(db);
            db = IntPtr.Zero;
        }
    }

    private static class Sql
    {
        private const string Lib = "winsqlite3.dll";
        [DllImport(Lib)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib)] internal static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
        [DllImport(Lib)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);
        [DllImport(Lib)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib)] internal static extern IntPtr sqlite3_backup_init(IntPtr destination, [MarshalAs(UnmanagedType.LPStr)] string dest, IntPtr source, [MarshalAs(UnmanagedType.LPStr)] string src);
        [DllImport(Lib)] internal static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Lib)] internal static extern int sqlite3_backup_finish(IntPtr backup);
    }
}
