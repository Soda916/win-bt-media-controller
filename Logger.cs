using System;
using System.Collections.Concurrent;
using System.Text;

namespace MediaController
{
    public static class Logger
    {
        private static readonly ConcurrentQueue<string> _logs = new();
        private const int MaxLogCount = 200;

        public static void Log(string message)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            _logs.Enqueue(entry);
            System.Diagnostics.Debug.WriteLine(entry);

            while (_logs.Count > MaxLogCount)
            {
                _logs.TryDequeue(out _);
            }
        }

        public static string GetLogText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== BT Media Controller Diagnostic Log ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"Runtime: .NET {Environment.Version}");
            sb.AppendLine("--------------------------------------------------");
            foreach (var log in _logs)
            {
                sb.AppendLine(log);
            }
            sb.AppendLine("==================================================");
            return sb.ToString();
        }

        public static void Clear()
        {
            _logs.Clear();
        }
    }
}
