using System;
using System.IO;
using System.Text;

namespace CheerfulGiverNXT.Infrastructure.Logging
{
    /// <summary>
    /// Minimal file logger for capturing exceptions (especially from MessageBoxes / UI handlers).
    /// Writes to: %LOCALAPPDATA%\CheerfulGiverNXT\Logs\CheerfulErrors.txt
    /// </summary>
    public static class ErrorLogger
    {
        private static readonly object _gate = new();

        public static string GetLogFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CheerfulGiverNXT",
                "Logs");

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "CheerfulErrors.txt");
        }

        public static string Log(Exception ex, string? context = null)
        {
            var path = GetLogFilePath();
            var correlationId = Guid.NewGuid().ToString("N");

            var sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine($"Timestamp   : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Correlation : {correlationId}");
            if (!string.IsNullOrWhiteSpace(context))
                sb.AppendLine($"Context     : {context}");
            sb.AppendLine("Exception   :");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();

            try
            {
                lock (_gate)
                {
                    File.AppendAllText(path, sb.ToString());
                }
            }
            catch
            {
                // ignore - last resort logging should never crash the app
            }

            return path;
        }
    }
}
