using System;
using System.IO;
using BepInEx;

namespace TransmutationTable
{
    /// <summary>
    /// 异常全量落盘：游戏内飘红的红字往往伴随 Unity 报错，但 LogOutput.log 可能被滚动覆盖。
    /// 本类的所有 catch 均把完整堆栈写到 BepInEx/config/TransmutationTable.errors.log。
    /// </summary>
    internal static class Diag
    {
        private static readonly object _lock = new object();

        public static void Error(string where, Exception e)
        {
            TransmutationTablePlugin.Log.LogError(where + ": " + e);
            Write(where, e.ToString() + "\n" + e.StackTrace);
        }

        public static void Warn(string where, string detail)
        {
            TransmutationTablePlugin.Log.LogWarning(where + ": " + detail);
            Write(where, detail);
        }

        private static void Write(string where, string detail)
        {
            try
            {
                lock (_lock)
                {
                    string path = Path.Combine(Paths.ConfigPath, "TransmutationTable.errors.log");
                    File.AppendAllText(path,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{where}]\n{detail}\n\n");
                }
            }
            catch
            {
                // 落盘失败只能静默
            }
        }
    }
}
