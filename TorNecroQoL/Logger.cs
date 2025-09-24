using System;
using System.IO;
using System.Text;

namespace TorNecroQoL
{
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private static bool _ready;
        public static string LogPath { get; private set; }

        public static void Init()
        {
            if (_ready) return;
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string moduleDir = Path.Combine(baseDir, "Mount and Blade II Bannerlord", "Modules", "TOR_NecroQoL");
                Directory.CreateDirectory(moduleDir);
                LogPath = Path.Combine(moduleDir, "TorNecroQoL.log");
                using (var s = File.AppendText(LogPath))
                    s.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss\\Z") + " Logger ready.");
                _ready = true;
            }
            catch
            {
                LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TorNecroQoL.log");
                try { using (var s = File.AppendText(LogPath)) s.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss\\Z") + " Logger ready."); } catch { }
                _ready = true;
            }
        }

        public static void Info(string msg)
        {
            try
            {
                if (!_ready) Init();
                lock (_lock)
                {
                    File.AppendAllText(LogPath, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss\\Z ") + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
