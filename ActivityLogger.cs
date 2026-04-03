using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioRecorder
{
    // ═══════════════════════════════════════════════════════════════════
    //  Entrée de log
    // ═══════════════════════════════════════════════════════════════════
    public class LogEntry
    {
        // Actions possibles :
        //   RecordingStarted | RecordingSaved | RecordingCancelled | RecordingError
        //   TranscriptionDone | SummaryDone | TranscriptionError
        [JsonPropertyName("ts")]          public DateTime Timestamp        { get; set; }
        [JsonPropertyName("action")]      public string   Action           { get; set; } = "";
        [JsonPropertyName("source")]      public string   Source           { get; set; } = "";  // Microphone | Loopback | Both
        [JsonPropertyName("filePath")]    public string   FilePath         { get; set; } = "";
        [JsonPropertyName("durSec")]      public double   DurationSec      { get; set; }         // durée enregistrement audio
        [JsonPropertyName("procDurSec")]  public double   ProcessingDurSec { get; set; }         // durée traitement (transcription ou résumé)
        [JsonPropertyName("words")]       public int      WordCount        { get; set; }         // mots (transcription ou résumé)
        [JsonPropertyName("mode")]        public string   Mode             { get; set; } = "";   // Local | Remote
        [JsonPropertyName("inLang")]      public string   InputLang        { get; set; } = "";   // en | fr  (langue audio)
        [JsonPropertyName("outLang")]     public string   OutputLang       { get; set; } = "";   // anglais | français (langue cible résumé)
        [JsonPropertyName("detail")]      public string   Detail           { get; set; } = "";   // message libre
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Statistiques calculées
    // ═══════════════════════════════════════════════════════════════════
    public class ActivityStats
    {
        // ── Enregistrements ───────────────────────────────────────────
        public int    RecordingCount  { get; set; }
        public double AvgDurationSec  { get; set; }

        // ── Transcriptions (global) ───────────────────────────────────
        public int    TranscriptionCount { get; set; }
        public double AvgTransWordCount  { get; set; }
        public double AvgTransDurSec     { get; set; }

        // ── Transcriptions Local ──────────────────────────────────────
        public int    TransLocalCount       { get; set; }
        public double AvgTransLocalDurSec   { get; set; }
        public int    TransLocalFrCount     { get; set; }
        public double AvgTransLocalFrDurSec { get; set; }
        public int    TransLocalEnCount     { get; set; }
        public double AvgTransLocalEnDurSec { get; set; }

        // ── Transcriptions Remote ─────────────────────────────────────
        public int    TransRemoteCount        { get; set; }
        public double AvgTransRemoteDurSec    { get; set; }
        public int    TransRemoteFrCount      { get; set; }
        public double AvgTransRemoteFrDurSec  { get; set; }
        public int    TransRemoteEnCount      { get; set; }
        public double AvgTransRemoteEnDurSec  { get; set; }

        // ── Résumés (global) ─────────────────────────────────────────
        public int    SummaryCount    { get; set; }
        public double AvgSumWordCount { get; set; }
        public double AvgSumDurSec    { get; set; }

        // ── Résumés par langue de sortie ──────────────────────────────
        public int    SumFrCount     { get; set; }
        public double AvgSumFrDurSec { get; set; }
        public int    SumEnCount     { get; set; }
        public double AvgSumEnDurSec { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Logger principal
    // ═══════════════════════════════════════════════════════════════════
    public class ActivityLogger
    {
        private readonly string    _logPath;
        private List<LogEntry>     _entries = new();

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Statistiques recalculées après chaque Log().</summary>
        public ActivityStats Stats { get; private set; } = new();

        /// <summary>Snapshot immuable de toutes les entrées (pour le visualiseur).</summary>
        public IReadOnlyList<LogEntry> Entries => _entries;

        // ── Construction ─────────────────────────────────────────────────
        public ActivityLogger(string logPath)
        {
            _logPath = logPath;
            Load();
            ComputeStats();
        }

        // ── API publique ─────────────────────────────────────────────────
        public void Log(string action,
                        string source           = "",
                        string filePath         = "",
                        double durationSec      = 0,
                        double processingDurSec = 0,
                        int    wordCount        = 0,
                        string mode             = "",
                        string inputLang        = "",
                        string outputLang       = "",
                        string detail           = "")
        {
            _entries.Add(new LogEntry
            {
                Timestamp        = DateTime.Now,
                Action           = action,
                Source           = source,
                FilePath         = filePath,
                DurationSec      = durationSec,
                ProcessingDurSec = processingDurSec,
                WordCount        = wordCount,
                Mode             = mode,
                InputLang        = inputLang,
                OutputLang       = outputLang,
                Detail           = detail
            });

            Save();
            ComputeStats();
        }

        // ── Persistence ───────────────────────────────────────────────────
        private void Load()
        {
            try
            {
                if (File.Exists(_logPath))
                {
                    string json = File.ReadAllText(_logPath, Encoding.UTF8);
                    _entries = JsonSerializer.Deserialize<List<LogEntry>>(json, _opts) ?? new();
                }
            }
            catch { _entries = new(); }
        }

        private void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_logPath,
                    JsonSerializer.Serialize(_entries, _opts),
                    Encoding.UTF8);
            }
            catch { }
        }

        // ── Calcul des stats ──────────────────────────────────────────────
        // Wrapper privé : met à jour la propriété Stats à partir de _entries.
        private void ComputeStats() => Stats = ComputeStats(_entries);

        /// <summary>
        /// Calcul pur des statistiques à partir d'une liste quelconque d'entrées.
        /// Internal pour être accessible au projet de tests (InternalsVisibleTo).
        /// </summary>
        internal static ActivityStats ComputeStats(IEnumerable<LogEntry> entries)
        {
            var saved  = entries.Where(e => e.Action == "RecordingSaved").ToList();
            var trans  = entries.Where(e => e.Action == "TranscriptionDone").ToList();
            var summ   = entries.Where(e => e.Action == "SummaryDone").ToList();

            // Sous-groupes transcription
            var transLocal    = trans.Where(e => e.Mode == "Local").ToList();
            var transRemote   = trans.Where(e => e.Mode == "Remote").ToList();
            var transLocalFr  = transLocal.Where(e => e.InputLang == "fr").ToList();
            var transLocalEn  = transLocal.Where(e => e.InputLang == "en").ToList();
            var transRemoteFr = transRemote.Where(e => e.InputLang == "fr").ToList();
            var transRemoteEn = transRemote.Where(e => e.InputLang == "en").ToList();

            // Sous-groupes résumés (langue de sortie)
            var summFr = summ.Where(e => e.OutputLang == "français").ToList();
            var summEn = summ.Where(e => e.OutputLang == "anglais").ToList();

            static double AvgD(List<LogEntry> lst, Func<LogEntry, double> sel)
                => lst.Count > 0 ? lst.Average(sel) : 0;
            static double AvgI(List<LogEntry> lst, Func<LogEntry, int> sel)
                => lst.Count > 0 ? lst.Average(e => (double)sel(e)) : 0;

            return new ActivityStats
            {
                RecordingCount  = saved.Count,
                AvgDurationSec  = AvgD(saved, e => e.DurationSec),

                TranscriptionCount = trans.Count,
                AvgTransWordCount  = AvgI(trans, e => e.WordCount),
                AvgTransDurSec     = AvgD(trans, e => e.ProcessingDurSec),

                TransLocalCount       = transLocal.Count,
                AvgTransLocalDurSec   = AvgD(transLocal,   e => e.ProcessingDurSec),
                TransLocalFrCount     = transLocalFr.Count,
                AvgTransLocalFrDurSec = AvgD(transLocalFr, e => e.ProcessingDurSec),
                TransLocalEnCount     = transLocalEn.Count,
                AvgTransLocalEnDurSec = AvgD(transLocalEn, e => e.ProcessingDurSec),

                TransRemoteCount       = transRemote.Count,
                AvgTransRemoteDurSec   = AvgD(transRemote,   e => e.ProcessingDurSec),
                TransRemoteFrCount     = transRemoteFr.Count,
                AvgTransRemoteFrDurSec = AvgD(transRemoteFr, e => e.ProcessingDurSec),
                TransRemoteEnCount     = transRemoteEn.Count,
                AvgTransRemoteEnDurSec = AvgD(transRemoteEn, e => e.ProcessingDurSec),

                SummaryCount    = summ.Count,
                AvgSumWordCount = AvgI(summ, e => e.WordCount),
                AvgSumDurSec    = AvgD(summ, e => e.ProcessingDurSec),

                SumFrCount     = summFr.Count,
                AvgSumFrDurSec = AvgD(summFr, e => e.ProcessingDurSec),
                SumEnCount     = summEn.Count,
                AvgSumEnDurSec = AvgD(summEn, e => e.ProcessingDurSec),
            };
        }
    }
}
