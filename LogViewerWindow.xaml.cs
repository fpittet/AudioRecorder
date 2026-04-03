using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace AudioRecorder
{
    // ── ViewModel ligne ───────────────────────────────────────────────────────
    public class LogEntryRow
    {
        private readonly LogEntry _e;

        public LogEntryRow(LogEntry e) { _e = e; }

        public DateTime Timestamp     => _e.Timestamp;
        public string   Action        => _e.Action;
        public string   Source        => _e.Source;
        public string   Mode          => _e.Mode;
        public string   InputLang     => _e.InputLang;
        public string   OutputLang    => _e.OutputLang;
        public string   Detail        => _e.Detail;

        public string FileNameOnly =>
            string.IsNullOrEmpty(_e.FilePath) ? "" : Path.GetFileName(_e.FilePath);

        public string DurationFmt =>
            _e.DurationSec > 0 ? FormatSec(_e.DurationSec) : "";

        public string ProcessingFmt =>
            _e.ProcessingDurSec > 0 ? FormatSec(_e.ProcessingDurSec) : "";

        public string WordsFmt =>
            _e.WordCount > 0 ? _e.WordCount.ToString() : "";

        private static string FormatSec(double sec)
        {
            if (sec < 60) return $"{sec:F0}s";
            return $"{(int)(sec / 60)}m{(int)(sec % 60):D2}s";
        }
    }

    // ── Fenêtre ────────────────────────────────────────────────────────────────
    public partial class LogViewerWindow : Window
    {
        public LogViewerWindow(IReadOnlyList<LogEntry> entries, string logFilePath)
        {
            InitializeComponent();

            TxtLogPath.Text   = logFilePath;
            TxtEntryCount.Text = $"{entries.Count} entrée{(entries.Count != 1 ? "s" : "")}";

            // Ordre antichronologique (plus récent en haut)
            LogGrid.ItemsSource = entries
                .Select(e => new LogEntryRow(e))
                .Reverse()
                .ToList();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}
