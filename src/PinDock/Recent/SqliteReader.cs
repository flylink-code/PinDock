using System.Runtime.InteropServices;
using System.Text;

namespace PinDock;

internal static class SqliteReader
{
    internal static string? TryQuerySqliteSingleValue(string dbPath, string sql)
    {
        try
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            if (sqlite3_open_v2(pathBytes, out IntPtr db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0)
                return null;
            try
            {
                byte[] sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
                if (sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out IntPtr stmt, IntPtr.Zero) != 0)
                    return null;
                try
                {
                    if (sqlite3_step(stmt) == 100 /* SQLITE_ROW */)
                    {
                        IntPtr textPtr = sqlite3_column_text(stmt, 0);
                        int byteLen = sqlite3_column_bytes(stmt, 0);
                        if (textPtr == IntPtr.Zero || byteLen <= 0)
                            return "";
                        byte[] buf = new byte[byteLen];
                        Marshal.Copy(textPtr, buf, 0, byteLen);
                        return Encoding.UTF8.GetString(buf);
                    }
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close(db);
            }
        }
        catch (DllNotFoundException)
        {
        }
        return null;
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr stmt);
}
