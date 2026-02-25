using CheerfulGiverNXT.Infrastructure.Logging;
using System;
using System.Text;
using System.Windows;

namespace CheerfulGiverNXT.Infrastructure.Ui
{
    /// <summary>
    /// Standardized UI error helper:
    /// = logs via Infrastructure\\Logging\\ErrorLogger.cs
    /// = shows a user-friendly MessageBox
    ///
    /// Use this in UI handlers (windows/viewmodels) instead of repeating:
    /// = try { ErrorLogger.Log(...) } catch {}
    /// = MessageBox.Show(...)
    /// </summary>
    public static class UiError
    {
        /// <summary>
        /// Logs the exception and shows a MessageBox.
        ///
        /// message:
        /// = optional friendly prefix shown before the exception message
        /// includeExceptionMessage:
        /// = false when you want a custom message only (the log path will still be shown)
        /// </summary>
        public static void Show(
            Exception ex,
            string title = "Error",
            string? context = null,
            string? message = null,
            bool includeExceptionMessage = true,
            MessageBoxImage icon = MessageBoxImage.Error,
            Window? owner = null)
        {
            string? logPath = null;
            try { logPath = ErrorLogger.Log(ex, context); }
            catch { /* ignore = last resort logging must not crash UI */ }

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(message))
                sb.Append(message.Trim());

            if (includeExceptionMessage)
            {
                if (sb.Length > 0)
                    sb.Append("\n\n");
                sb.Append(ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(logPath))
                sb.Append("\n\nLogged to:\n").Append(logPath);

            var body = sb.Length > 0 ? sb.ToString() : ex.Message;

            if (owner is not null)
                MessageBox.Show(owner, body, title, MessageBoxButton.OK, icon);
            else
                MessageBox.Show(body, title, MessageBoxButton.OK, icon);
        }
    }
}
