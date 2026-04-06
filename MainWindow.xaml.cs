using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Lame;
using Whisper.net;
using Whisper.net.Ggml;

namespace AudioRecorder
{
    public partial class MainWindow : Window
    {
        // ── Capture — mode simple (micro OU loopback) ────────────────────────
        private IWaveIn?           _captureDevice;
        private LameMP3FileWriter? _mp3Writer;
        private string             _tempFilePath = string.Empty;

        // ── Capture — mode "Les deux" (micro ET loopback simultanés) ────────
        private IWaveIn?        _captureDevice2;
        private WaveFileWriter? _wavWriter;
        private WaveFileWriter? _wavWriter2;
        private string          _tempMicWavPath  = string.Empty;
        private string          _tempLoopWavPath = string.Empty;
        private bool            _isBothMode;
        private Exception?      _bothStopError;

        // Erreur éventuelle capturée par les handlers RecordingStopped
        // (écrit depuis le handler, lu depuis FinishBothModeAsync sur le UI thread).

        // ── Alignement temporel "Les deux" ───────────────────────────────────
        // WasapiLoopbackCapture ne déclenche DataAvailable que quand du son
        // arrive réellement ; les silences sont donc absents du WAV loopback.
        // À chaque DataAvailable on calcule combien d'octets auraient dû être
        // écrits depuis le démarrage et on comble le manque avec du silence.
        private readonly Stopwatch _bothRecordingSw      = new();
        private long               _loopbackBytesWritten;   // octets réellement écrits dans wav2

        // ── Timer & durée d'enregistrement ──────────────────────────────────
        // DispatcherTimer accumule un tick de 100 ms mais peut dériver si le
        // UI thread est chargé. On utilise un Stopwatch pour la durée réelle.
        private readonly DispatcherTimer _timer       = new();
        private readonly Stopwatch       _recordingSw = new();
        private TimeSpan                 _targetDuration;         // Zero = pas de limite

        // ── Auto-save / auto-transcription (arrêt programmé) ────────────────
        private string _autoSavePath            = string.Empty;  // non-vide → pas de dialogue
        private bool   _autoTranscribeAfterSave;

        // ── Vumètre ──────────────────────────────────────────────────────────
        private double       _smoothedLevel = 0;
        private const double SmoothFactor   = 0.4;

        // ── Transcription / Résumé ───────────────────────────────────────────
        private string _lastSavedFilePath = string.Empty;

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        // Les secrets sont chargés depuis appsettings.json (exclu du dépôt Git).
        // Voir appsettings.example.json pour le format attendu.
        private static readonly string InfApiToken   = AppSettings.InfApiToken;
        private static readonly int    InfProductId  = AppSettings.InfProductId;
        private static readonly string TranscribeUrl = AppSettings.TranscribeUrl;

        private static readonly string _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder", "models");
        private static readonly string _modelPath = Path.Combine(_modelDir, "ggml-base.bin");

        // ── Logger (chemin = dossier projet) ────────────────────────────────
        private static readonly string _projectDir = FindProjectDir();
        private static readonly string _logPath    = Path.Combine(_projectDir, "activity_log.json");
        private readonly ActivityLogger _logger;

        // ── Persistance du dernier répertoire ────────────────────────────────
        private static readonly string _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder", "prefs.json");

        private string LastSaveDirectory
        {
            get
            {
                try
                {
                    if (File.Exists(_configPath))
                    {
                        string json  = File.ReadAllText(_configPath);
                        string saved = JsonDocument.Parse(json)
                                           .RootElement
                                           .GetProperty("lastSaveDirectory")
                                           .GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                            return saved;
                    }
                }
                catch { }
                return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                    File.WriteAllText(_configPath,
                        JsonSerializer.Serialize(new { lastSaveDirectory = value }));
                }
                catch { }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Chemin de base pour les données (log + temp)
        //  • Dev  : remonte depuis bin\Debug\net8.0-windows\ jusqu'au *.csproj
        //  • Publié : aucun *.csproj trouvé → dossier à côté de l'exe
        // ════════════════════════════════════════════════════════════════════
        private static string FindProjectDir()
        {
            var dir   = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            int depth = 0;
            while (dir != null && dir.GetFiles("*.csproj").Length == 0 && depth < 5)
            {
                dir = dir.Parent;
                depth++;
            }
            return (dir != null && dir.GetFiles("*.csproj").Length > 0)
                   ? dir.FullName
                   : AppDomain.CurrentDomain.BaseDirectory;
        }

        // ════════════════════════════════════════════════════════════════════
        //  CONSTRUCTEUR
        // ════════════════════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick    += Timer_Tick;

            _logger = new ActivityLogger(_logPath);
            RefreshStats();
        }

        // ════════════════════════════════════════════════════════════════════
        //  BOUTON START
        // ════════════════════════════════════════════════════════════════════
        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isBothMode = RbBoth.IsChecked == true;
                string source = _isBothMode ? "Both"
                              : RbLoopback.IsChecked == true ? "Loopback" : "Microphone";

                if (_isBothMode)
                    StartBothMode();
                else
                    StartSingleMode(useLoopback: RbLoopback.IsChecked == true);

                _logger.Log("RecordingStarted", source: source);

                BtnStart.IsEnabled           = false;
                BtnStop.IsEnabled            = true;
                RbMicrophone.IsEnabled       = false;
                RbLoopback.IsEnabled         = false;
                RbBoth.IsEnabled             = false;
                BtnResume.IsEnabled          = false;
                CbOutputLang.IsEnabled       = false;
                CbAudioLang.IsEnabled        = false;
                RbTranscribeLocal.IsEnabled  = false;
                RbTranscribeRemote.IsEnabled = false;
                TxtStatus.Text               = "● EN COURS";
                TxtStatus.Foreground         = System.Windows.Media.Brushes.LimeGreen;
                _targetDuration              = ParseTargetDuration(TxtDuration.Text);
                _recordingSw.Restart();
                TxtTimer.Text                = "00:00:00";
                TxtDuration.IsEnabled        = false;
                BtnOpenMp3.IsEnabled         = false;
                _timer.Start();
            }
            catch (Exception ex)
            {
                _logger.Log("RecordingError", detail: ex.Message);
                MessageBox.Show($"Impossible de démarrer l'enregistrement :\n\n{ex.Message}",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                CleanupRecorder();
            }
        }

        // ── Mode simple : micro OU loopback ──────────────────────────────────
        private void StartSingleMode(bool useLoopback)
        {
            _tempFilePath = TempMp3Path();
            WaveFormat fmt;

            if (useLoopback)
            {
                var lb  = new WasapiLoopbackCapture();
                fmt     = lb.WaveFormat;
                _captureDevice = lb;
            }
            else
            {
                if (WaveIn.DeviceCount == 0)
                    throw new InvalidOperationException("Aucun microphone détecté.");
                var mic = new WaveInEvent
                {
                    DeviceNumber       = 0,
                    WaveFormat         = new WaveFormat(44100, 16, 2),
                    BufferMilliseconds = 100
                };
                fmt = mic.WaveFormat;
                _captureDevice = mic;
            }

            _mp3Writer = new LameMP3FileWriter(_tempFilePath, fmt, LAMEPreset.STANDARD);
            _captureDevice.DataAvailable    += SingleDevice_DataAvailable;
            _captureDevice.RecordingStopped += SingleDevice_RecordingStopped;
            _captureDevice.StartRecording();
        }

        // ── Mode "Les deux" ──────────────────────────────────────────────────
        //
        //  Coordination d'arrêt : BtnStop_Click nullifie les writers, appelle
        //  StopRecording, attend 500ms (Task.Delay), puis ferme les fichiers
        //  explicitement — sans attendre que RecordingStopped se déclenche.
        //  Les handlers RecordingStopped se contentent de logger.
        // ────────────────────────────────────────────────────────────────────
        private void StartBothMode()
        {
            if (WaveIn.DeviceCount == 0)
                throw new InvalidOperationException("Aucun microphone détecté.");

            Directory.CreateDirectory(_tempDir);
            string stamp     = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _tempMicWavPath  = Path.Combine(_tempDir, $"rec_mic_{stamp}.wav");
            _tempLoopWavPath = Path.Combine(_tempDir, $"rec_loop_{stamp}.wav");

            var mic      = new WaveInEvent
            {
                DeviceNumber       = 0,
                WaveFormat         = new WaveFormat(44100, 16, 2),
                BufferMilliseconds = 100
            };
            var loopback = new WasapiLoopbackCapture();

            _wavWriter  = new WaveFileWriter(_tempMicWavPath,  mic.WaveFormat);
            _wavWriter2 = new WaveFileWriter(_tempLoopWavPath, loopback.WaveFormat);

            _captureDevice  = mic;
            _captureDevice2 = loopback;

            // Réinitialiser l'erreur et le compteur d'alignement
            _bothStopError        = null;
            _loopbackBytesWritten = 0;

            // ── Device 1 : microphone ────────────────────────────────────────
            _captureDevice.DataAvailable += (_, ev) =>
            {
                try { _wavWriter?.Write(ev.Buffer, 0, ev.BytesRecorded); } catch { }
                double rms = CalculateRms(ev.Buffer, ev.BytesRecorded, 16);
                Dispatcher.BeginInvoke(() => UpdateVuMeter(rms));
            };
            _captureDevice.RecordingStopped += (_, ev) =>
            {
                if (ev.Exception != null) _bothStopError = ev.Exception;
                _logger.Log("DbgRS_Mic", detail: ev.Exception?.Message ?? "ok");
            };

            // ── Device 2 : loopback ──────────────────────────────────────────
            _captureDevice2.DataAvailable += (_, ev) =>
            {
                // ── Alignement continu : combler les silences non-capturés ───
                // WasapiLoopbackCapture ne fire DataAvailable que quand du son
                // arrive réellement ; les silences sont absents du flux.
                // À chaque callback, on calcule combien d'octets auraient dû
                // être écrits jusqu'au DÉBUT de ce buffer (= elapsedMs au moment
                // de l'appel, moins la durée du buffer lui-même) et on comble
                // la différence avec du silence.
                var fmt = _captureDevice2?.WaveFormat;
                if (fmt != null)
                {
                    long elapsedMs   = _bothRecordingSw.ElapsedMilliseconds;
                    // Octets attendus juste AVANT ce buffer
                    long expectedPre = (long)(elapsedMs / 1000.0 * fmt.AverageBytesPerSecond)
                                       - ev.BytesRecorded;
                    expectedPre = Math.Max(0L, (expectedPre / fmt.BlockAlign) * fmt.BlockAlign);

                    long gap = expectedPre - _loopbackBytesWritten;
                    if (gap > 0)
                    {
                        gap = (gap / fmt.BlockAlign) * fmt.BlockAlign;
                        try
                        {
                            _wavWriter2?.Write(new byte[gap], 0, (int)gap);
                            _loopbackBytesWritten += gap;
                        }
                        catch { }

                        long gapMs = gap * 1000L / fmt.AverageBytesPerSecond;
                        if (gapMs > 100)
                            Dispatcher.BeginInvoke(() =>
                                _logger.Log("DbgLoopback_GapFilled",
                                    detail: $"gap={gapMs} ms ({gap} octets) @ t={elapsedMs} ms"));
                    }
                }

                try
                {
                    _wavWriter2?.Write(ev.Buffer, 0, ev.BytesRecorded);
                    _loopbackBytesWritten += ev.BytesRecorded;
                }
                catch { }

                double rms = CalculateRms(ev.Buffer, ev.BytesRecorded,
                                          _captureDevice2?.WaveFormat?.BitsPerSample ?? 32);
                Dispatcher.BeginInvoke(() => UpdateVuMeter(rms));
            };
            _captureDevice2.RecordingStopped += (_, ev) =>
            {
                if (ev.Exception != null) _bothStopError = ev.Exception;
                _logger.Log("DbgRS_Loop", detail: ev.Exception?.Message ?? "ok");
            };

            // Démarrer le chrono JUSTE AVANT StartRecording pour mesurer
            // le délai le plus précisément possible.
            _bothRecordingSw.Restart();
            _captureDevice.StartRecording();
            _captureDevice2.StartRecording();
        }

        // ════════════════════════════════════════════════════════════════════
        //  BOUTON STOP
        // ════════════════════════════════════════════════════════════════════
        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            BtnStop.IsEnabled    = false;
            _timer.Stop();
            _recordingSw.Stop();   // figer la durée avant le mixage éventuel
            TxtStatus.Text       = _isBothMode ? "Arrêt + mixage en cours…" : "Arrêt en cours…";
            TxtStatus.Foreground = System.Windows.Media.Brushes.Gray;

            if (_isBothMode)
            {
                // ── Stratégie : ne PAS attendre RecordingStopped (jamais fiable) ──
                //
                // 1. Capturer les writers et les nullifier → les DataAvailable
                //    ultérieurs feront un no-op (null-check).
                // 2. Appeler StopRecording sur les deux devices.
                // 3. await Task.Delay(500) : libère le UI thread pour drainer
                //    les derniers callbacks DataAvailable encore en file.
                // 4. Fermer explicitement les writers.
                // 5. Mixer et sauvegarder.

                _logger.Log("DbgStop_Start",
                    detail: $"mic={_tempMicWavPath}  loop={_tempLoopWavPath}");

                // — Étape 1 : désactiver les écritures —
                var w1 = _wavWriter;  _wavWriter  = null;
                var w2 = _wavWriter2; _wavWriter2 = null;

                // — Étape 2 : signaler l'arrêt aux devices —
                Exception? stopEx1 = null, stopEx2 = null;
                try { _captureDevice?.StopRecording(); }
                catch (Exception ex) { stopEx1 = ex; }
                try { _captureDevice2?.StopRecording(); }
                catch (Exception ex) { stopEx2 = ex; }

                _logger.Log("DbgStop_StopCalled",
                    detail: $"dev1={stopEx1?.Message ?? "ok"}  dev2={stopEx2?.Message ?? "ok"}");

                // — Étape 3 : laisser le UI thread drainer les callbacks restants —
                await Task.Delay(500);

                // — Étape 4 : fermer les fichiers WAV —
                Exception? flushEx = null;
                try { if (w1 != null) { await w1.FlushAsync(); await w1.DisposeAsync(); } } catch (Exception ex) { flushEx = ex; }
                try { if (w2 != null) { await w2.FlushAsync(); await w2.DisposeAsync(); } } catch (Exception ex) { flushEx ??= ex; }

                long sz1 = FileSizeBytes(_tempMicWavPath);
                long sz2 = FileSizeBytes(_tempLoopWavPath);
                _logger.Log("DbgStop_WritersClosed",
                    detail: $"mic={sz1} bytes  loop={sz2} bytes  " +
                            $"flushErr={flushEx?.Message ?? "none"}");

                // — Étape 5 : mixer —
                await FinishBothModeAsync(stopEx1 ?? stopEx2 ?? _bothStopError);
            }
            else
            {
                // Mode simple : RecordingStopped → SingleDevice_RecordingStopped
                _captureDevice?.StopRecording();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  DONNÉES AUDIO — mode simple
        // ════════════════════════════════════════════════════════════════════
        private void SingleDevice_DataAvailable(object? sender, WaveInEventArgs e)
        {
            try { _mp3Writer?.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
            double rms = CalculateRms(e.Buffer, e.BytesRecorded,
                                      _captureDevice?.WaveFormat?.BitsPerSample ?? 16);
            Dispatcher.BeginInvoke(() => UpdateVuMeter(rms));
        }

        // ════════════════════════════════════════════════════════════════════
        //  FIN D'ENREGISTREMENT — mode simple (thread NAudio → Post → UI)
        // ════════════════════════════════════════════════════════════════════
        private void SingleDevice_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try { _mp3Writer?.Flush();   } catch { }
                try { _mp3Writer?.Dispose(); } catch { }
                _mp3Writer = null;

                try { _captureDevice?.Dispose(); } catch { }
                _captureDevice = null;

                VuMeter.Width  = 0;
                _smoothedLevel = 0;

                if (e.Exception != null)
                {
                    _logger.Log("RecordingError", detail: e.Exception.Message);
                    MessageBox.Show($"Erreur pendant l'enregistrement :\n\n{e.Exception.Message}",
                                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetUI();
                    return;
                }

                ShowSaveDialog();
            });
        }

        // ════════════════════════════════════════════════════════════════════
        //  MIXAGE + FIN — mode "Les deux" (déjà sur UI thread via await)
        // ════════════════════════════════════════════════════════════════════
        private async Task FinishBothModeAsync(Exception? stopError)
        {
            _logger.Log("DbgFinish_Start",
                detail: $"stopError={stopError?.Message ?? "null"}  " +
                        $"mic={FileSizeBytes(_tempMicWavPath)}b  " +
                        $"loop={FileSizeBytes(_tempLoopWavPath)}b");

            try { _captureDevice?.Dispose();  } catch { } _captureDevice  = null;
            try { _captureDevice2?.Dispose(); } catch { } _captureDevice2 = null;

            VuMeter.Width  = 0;
            _smoothedLevel = 0;

            if (stopError != null)
            {
                _logger.Log("RecordingError", source: "Both", detail: stopError.Message);
                MessageBox.Show($"Erreur pendant l'enregistrement :\n\n{stopError.Message}",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                // Fichiers WAV conservés dans temp\ pour inspection
                ResetUI();
                return;
            }

            TxtStatus.Text = "Mixage des pistes en cours…";

            string mixedMp3 = TempMp3Path("mixed");
            try
            {
                string mic  = _tempMicWavPath;
                string loop = _tempLoopWavPath;
                _logger.Log("DbgFinish_MixStart",
                    detail: $"out={mixedMp3}");
                await Task.Run(() => MixAndEncodeToMp3(mic, loop, mixedMp3));
                _logger.Log("DbgFinish_MixDone",
                    detail: $"mp3={FileSizeBytes(mixedMp3)}b");
                _tempFilePath = mixedMp3;
            }
            catch (Exception ex)
            {
                _logger.Log("RecordingError", source: "Both", detail: $"Mixage : {ex.Message}");
                MessageBox.Show($"Erreur lors du mixage :\n\n{ex.Message}",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                TryDeleteFile(mixedMp3);
                ResetUI();
                return;
            }
            finally
            {
                // Fichiers WAV conservés dans temp\ pour inspection
            }

            _logger.Log("DbgFinish_ShowDialog");
            ShowSaveDialog();
        }

        // ════════════════════════════════════════════════════════════════════
        //  SAUVEGARDE DU FICHIER MP3
        // ════════════════════════════════════════════════════════════════════
        private void ShowSaveDialog()
        {
            string source      = _isBothMode ? "Both"
                               : RbLoopback.IsChecked == true ? "Loopback" : "Microphone";
            double durationSec = _recordingSw.Elapsed.TotalSeconds;

            if (!string.IsNullOrEmpty(_autoSavePath))
            {
                // ── Mode auto-save : pas de dialogue ────────────────────────
                try
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(_autoSavePath)!);
                    File.Move(_tempFilePath, _autoSavePath, overwrite: true);
                    _lastSavedFilePath     = _autoSavePath;
                    TxtFileInfo.Text       = _autoSavePath;
                    TxtFileInfo.Foreground = System.Windows.Media.Brushes.LimeGreen;

                    _logger.Log("RecordingSaved",
                        source:      source,
                        filePath:    _autoSavePath,
                        durationSec: durationSec,
                        detail:      "auto-save");
                    RefreshStats();
                }
                catch (Exception ex)
                {
                    _logger.Log("RecordingError", source: source,
                        detail: $"Auto-save : {ex.Message}");
                    MessageBox.Show($"Impossible de sauvegarder automatiquement :\n\n{ex.Message}",
                                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                _autoSavePath = string.Empty;
            }
            else
            {
                // ── Mode manuel : boîte de dialogue ─────────────────────────
                var dlg = new SaveFileDialog
                {
                    Title            = "Enregistrer le fichier audio",
                    Filter           = "Fichier MP3 (*.mp3)|*.mp3|Tous les fichiers (*.*)|*.*",
                    DefaultExt       = ".mp3",
                    FileName         = $"enregistrement_{DateTime.Now:yyyyMMdd_HHmmss}",
                    InitialDirectory = LastSaveDirectory
                };

                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        File.Move(_tempFilePath, dlg.FileName, overwrite: true);
                        LastSaveDirectory      = Path.GetDirectoryName(dlg.FileName)
                                                 ?? LastSaveDirectory;
                        _lastSavedFilePath     = dlg.FileName;
                        TxtFileInfo.Text       = dlg.FileName;
                        TxtFileInfo.Foreground = System.Windows.Media.Brushes.LimeGreen;

                        _logger.Log("RecordingSaved",
                            source:      source,
                            filePath:    dlg.FileName,
                            durationSec: durationSec);
                        RefreshStats();
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("RecordingError", source: source,
                            detail: $"Sauvegarde : {ex.Message}");
                        MessageBox.Show($"Impossible de sauvegarder :\n\n{ex.Message}",
                                        "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                        TxtFileInfo.Text = _tempFilePath + "  (temporaire)";
                    }
                }
                else
                {
                    _logger.Log("RecordingCancelled", source: source);
                    TryDeleteTemp();
                    TxtFileInfo.Text       = "Sauvegarde annulée";
                    TxtFileInfo.Foreground = System.Windows.Media.Brushes.Gray;
                }
            }

            ResetUI();

            // ── Auto-transcription après auto-save ───────────────────────────
            if (_autoTranscribeAfterSave && !string.IsNullOrEmpty(_lastSavedFilePath))
            {
                _autoTranscribeAfterSave = false;
                string outputLang = (CbOutputLang.SelectedItem as ComboBoxItem)
                                    ?.Tag?.ToString() ?? "anglais";
                _ = RunTranscribeAndSummarize(outputLang);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  MIXAGE DEUX PISTES WAV → MP3
        // ════════════════════════════════════════════════════════════════════
        private static void MixAndEncodeToMp3(string micWavPath, string loopWavPath, string mp3Out)
        {
            using var r1 = new AudioFileReader(micWavPath);
            using var r2 = new AudioFileReader(loopWavPath);

            int targetRate = Math.Max(r1.WaveFormat.SampleRate, r2.WaveFormat.SampleRate);

            ISampleProvider sp1 = r1;
            ISampleProvider sp2 = r2;

            if (r1.WaveFormat.SampleRate != targetRate)
                sp1 = new WdlResamplingSampleProvider(sp1, targetRate);
            if (r2.WaveFormat.SampleRate != targetRate)
                sp2 = new WdlResamplingSampleProvider(sp2, targetRate);

            if (sp1.WaveFormat.Channels == 1) sp1 = new MonoToStereoSampleProvider(sp1);
            if (sp2.WaveFormat.Channels == 1) sp2 = new MonoToStereoSampleProvider(sp2);

            // IMPORTANT : ReadFully = false (défaut NAudio).
            // Avec ReadFully = true, le mixer renvoie toujours « count » samples
            // même quand les deux sources sont épuisées → boucle infinie.
            var mixer  = new MixingSampleProvider(new[] { sp1, sp2 });   // ReadFully = false
            var wave16 = new SampleToWaveProvider16(mixer);

            using var mp3w = new LameMP3FileWriter(mp3Out, wave16.WaveFormat, LAMEPreset.STANDARD);
            var buf = new byte[targetRate * 4];
            int read;
            while ((read = wave16.Read(buf, 0, buf.Length)) > 0)
                mp3w.Write(buf, 0, read);
        }

        // ════════════════════════════════════════════════════════════════════
        //  TIMER
        // ════════════════════════════════════════════════════════════════════
        private void Timer_Tick(object? sender, EventArgs e)
        {
            var elapsed   = _recordingSw.Elapsed;
            TxtTimer.Text = elapsed.ToString(@"hh\:mm\:ss");

            // ── Arrêt automatique ─────────────────────────────────────────
            if (_targetDuration > TimeSpan.Zero && elapsed >= _targetDuration)
            {
                var triggered = _targetDuration;
                _targetDuration = TimeSpan.Zero;   // désarmer (un seul déclenchement)
                _autoSavePath   = Path.Combine(LastSaveDirectory,
                                    $"enregistrement_{DateTime.Now:yyyyMMdd_HHmmss}.mp3");
                _autoTranscribeAfterSave = true;
                _logger.Log("AutoStopTriggered",
                    detail: $"target={FormatDuration(triggered.TotalSeconds)}  " +
                            $"savePath={_autoSavePath}");
                BtnStop_Click(this, new RoutedEventArgs());
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  VUMÈTRE
        // ════════════════════════════════════════════════════════════════════
        private static double CalculateRms(byte[] buffer, int bytesRecorded, int bitsPerSample)
        {
            if (bytesRecorded < 2) return 0;
            double sum = 0; int count = 0;

            if (bitsPerSample == 32)
            {
                for (int i = 0; i < bytesRecorded - 3; i += 4)
                { float s = BitConverter.ToSingle(buffer, i); sum += s * s; count++; }
            }
            else
            {
                for (int i = 0; i < bytesRecorded - 1; i += 2)
                { short s = (short)(buffer[i] | (buffer[i+1] << 8));
                  double n = s / 32768.0; sum += n * n; count++; }
            }
            return count > 0 ? Math.Sqrt(sum / count) : 0;
        }

        private void UpdateVuMeter(double rms)
        {
            double amp   = Math.Min(rms * 5.0, 1.0);
            _smoothedLevel = (_smoothedLevel * (1 - SmoothFactor)) + (amp * SmoothFactor);
            double w = VuBorder.ActualWidth;
            if (w > 0) VuMeter.Width = Math.Max(_smoothedLevel * w, 0);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SÉLECTEUR + BOUTON RÉSUMER
        // ════════════════════════════════════════════════════════════════════
        private void CbOutputLang_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (BtnResume == null) return;   // garde pendant l'initialisation XAML
            var tag = (CbOutputLang.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            BtnResume.Content = tag == "français" ? "Résume" : "Summarize";
        }

        private async void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            var tag = (CbOutputLang.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                      ?? "anglais";
            await RunTranscribeAndSummarize(tag);
        }

        private async Task RunTranscribeAndSummarize(string outputLang)
        {
            if (string.IsNullOrEmpty(_lastSavedFilePath) || !File.Exists(_lastSavedFilePath))
            {
                MessageBox.Show("Aucun fichier audio disponible.",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnResume.IsEnabled   = false;
            TxtSummary.Text       = string.Empty;

            var    selItem       = CbAudioLang.SelectedItem as ComboBoxItem;
            string audioCode     = selItem?.Tag?.ToString() ?? "en";        // "en" | "fr"
            string audioLangText = audioCode == "en" ? "anglais" : "français";
            bool   useLocal      = RbTranscribeLocal.IsChecked == true;
            string mode          = useLocal ? "Local" : "Remote";

            string audioFileName = Path.GetFileNameWithoutExtension(_lastSavedFilePath);
            string saveDir       = Path.GetDirectoryName(_lastSavedFilePath)!;

            _logger.Log("TranscriptionStarted",
                filePath:   _lastSavedFilePath,
                mode:       mode,
                inputLang:  audioCode,
                outputLang: outputLang);

            try
            {
                // ── Étape 1 : Transcription ──────────────────────────────────
                TxtStatus.Text  = "Transcription en cours…";
                TxtSummary.Text = "Transcription en cours…";

                var    transcribeSw    = Stopwatch.StartNew();
                string transcribedText = useLocal
                    ? await TranscribeLocalAsync(_lastSavedFilePath, audioCode)
                    : await TranscribeInfomaniakAsync(_lastSavedFilePath, audioCode);
                transcribeSw.Stop();

                int    transWordCount   = CountWords(transcribedText);
                double transcribeDurSec = transcribeSw.Elapsed.TotalSeconds;

                string transcribedPath = WriteTextToFile(
                    transcribedText,
                    Path.Combine(saveDir, $"transcribed_{audioFileName}.txt"));

                _logger.Log("TranscriptionDone",
                    filePath:          transcribedPath,
                    processingDurSec:  transcribeDurSec,
                    wordCount:         transWordCount,
                    mode:              mode,
                    inputLang:         audioCode,
                    outputLang:        audioLangText);
                RefreshStats();

                // ── Étape 2 : Résumé ─────────────────────────────────────────
                TxtStatus.Text  = "Résumé en cours…";
                TxtSummary.Text = "Résumé en cours…";

                var    summarySw    = Stopwatch.StartNew();
                string summarizedText = await SummarizeInfomaniakAsync(
                    transcribedText, audioLangText, outputLang);
                summarySw.Stop();

                int    sumWordCount  = CountWords(summarizedText);
                double summaryDurSec = summarySw.Elapsed.TotalSeconds;

                string summarizedPath = WriteTextToFile(
                    summarizedText,
                    Path.Combine(saveDir, $"summerized_{audioFileName}.txt"));

                _logger.Log("SummaryDone",
                    filePath:         summarizedPath,
                    processingDurSec: summaryDurSec,
                    wordCount:        sumWordCount,
                    inputLang:        audioCode,
                    outputLang:       outputLang);
                RefreshStats();

                TxtSummary.Text = summarizedText;
                TxtStatus.Text  =
                    $"Transcription ({transWordCount} mots, {FormatSec(transcribeDurSec)})" +
                    $"  |  Résumé ({sumWordCount} mots, {FormatSec(summaryDurSec)})";

                // ── Annonce vocale ───────────────────────────────────────────
                string ttsText = outputLang == "anglais" ? "Summary completed" : "Résumé terminé";
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var synth = new SpeechSynthesizer();
                        synth.Volume = 100;
                        synth.Speak(ttsText);
                    }
                    catch { /* TTS non disponible, on ignore */ }
                });
            }
            catch (Exception ex)
            {
                _logger.Log("TranscriptionError", detail: ex.Message);
                TxtSummary.Text = $"Erreur : {ex.Message}";
                TxtStatus.Text  = "Erreur";
                MessageBox.Show($"Erreur lors de la transcription/résumé :\n\n{ex.Message}",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnResume.IsEnabled = true;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  BOUTON OUVRIR MP3
        // ════════════════════════════════════════════════════════════════════
        private void BtnOpenMp3_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title            = "Sélectionner un fichier MP3 à transcrire",
                Filter           = "Fichiers MP3 (*.mp3)|*.mp3|Tous les fichiers (*.*)|*.*",
                InitialDirectory = LastSaveDirectory
            };

            if (dlg.ShowDialog() == true)
            {
                _lastSavedFilePath     = dlg.FileName;
                TxtFileInfo.Text       = dlg.FileName;
                TxtFileInfo.Foreground = System.Windows.Media.Brushes.LimeGreen;
                BtnResume.IsEnabled    = true;
                _logger.Log("Mp3FileOpened", filePath: dlg.FileName);
                RefreshStats();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  BOUTON VOIR LE LOG
        // ════════════════════════════════════════════════════════════════════
        private void BtnShowLog_Click(object sender, RoutedEventArgs e)
        {
            var win = new LogViewerWindow(_logger.Entries, _logPath)
            {
                Owner = this
            };
            win.Show();
        }

        // ════════════════════════════════════════════════════════════════════
        //  TRANSCRIPTION LOCALE (Whisper.net)
        // ════════════════════════════════════════════════════════════════════
        private async Task<string> TranscribeLocalAsync(string audioFilePath, string lang)
        {
            if (!File.Exists(_modelPath))
            {
                TxtStatus.Text  = "Téléchargement du modèle Whisper (première utilisation)…";
                TxtSummary.Text = "Téléchargement du modèle Whisper base (~140 Mo) — veuillez patienter…";
                Directory.CreateDirectory(_modelDir);
                using var ms = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.Base);
                using var fw = File.OpenWrite(_modelPath);
                await ms.CopyToAsync(fw);
            }

            return await Task.Run(async () =>
            {
                using var wav       = ConvertToWhisperWav(audioFilePath);
                using var factory   = WhisperFactory.FromPath(_modelPath);
                using var processor = factory.CreateBuilder().WithLanguage(lang).Build();
                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(wav))
                    sb.Append(seg.Text);
                return sb.ToString();
            });
        }

        private static MemoryStream ConvertToWhisperWav(string mp3Path)
        {
            using var reader    = new Mp3FileReader(mp3Path);
            var fmt             = new WaveFormat(16000, 16, 1);
            using var resampler = new NAudio.Wave.MediaFoundationResampler(reader, fmt)
            { ResamplerQuality = 60 };
            var ms = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(ms, resampler);
            ms.Position = 0;
            return ms;
        }

        // ════════════════════════════════════════════════════════════════════
        //  TRANSCRIPTION DISTANTE (Infomaniak Whisper)
        // ════════════════════════════════════════════════════════════════════
        private static async Task<string> TranscribeInfomaniakAsync(string filePath, string lang)
        {
            using var content = new MultipartFormDataContent();
            using var fs      = File.OpenRead(filePath);
            var fc            = new StreamContent(fs);
            fc.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            content.Add(fc, "file", Path.GetFileName(filePath));
            if (!string.IsNullOrEmpty(lang))
                content.Add(new StringContent(lang), "language");

            var resp = await _httpClient.PostAsync(TranscribeUrl, content);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
        }

        // ════════════════════════════════════════════════════════════════════
        //  RÉSUMÉ VIA LLM INFOMANIAK (qwen3)
        // ════════════════════════════════════════════════════════════════════
        private static async Task<string> SummarizeInfomaniakAsync(
            string text, string inputLang, string outputLang)
        {
            string url    = $"https://api.infomaniak.com/2/ai/{InfProductId}/openai/v1/chat/completions";
            string prompt = $"Résume ce texte {inputLang} en {outputLang}:\n{text}\n";
            int    maxTok = CalculateMaxTokens(EstimateTokens(text));

            var payload = new
            {
                model    = "qwen3",
                messages = new[]
                {
                    new { role = "system",
                          content = "Tu es un assistant qui résume les textes de manière claire et concise." },
                    new { role = "user", content = prompt }
                },
                max_tokens  = maxTok,
                temperature = 0.7
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload),
                                            Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", InfApiToken);

            var resp = await _httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? string.Empty;
        }

        // ════════════════════════════════════════════════════════════════════
        //  STATISTIQUES — affichage
        // ════════════════════════════════════════════════════════════════════
        private void RefreshStats()
        {
            var s = _logger.Stats;

            // ── Enregistrements ──────────────────────────────────────────────
            TxtStatsRec.Text = s.RecordingCount == 0
                ? "0  (durée moy. —)"
                : $"{s.RecordingCount}  (durée moy. {FormatDuration(s.AvgDurationSec)})";

            // ── Transcriptions ───────────────────────────────────────────────
            if (s.TranscriptionCount == 0)
            {
                TxtStatsTrans.Text       = "0  (moy. — mots, durée —)";
                TxtStatsTransDetail.Text = "Local: 0 (—)  ·  Remote: 0 (—)";
            }
            else
            {
                TxtStatsTrans.Text =
                    $"{s.TranscriptionCount}  " +
                    $"(moy. {s.AvgTransWordCount:F0} mots, {FormatSec(s.AvgTransDurSec)})";

                string locDetail = s.TransLocalCount == 0 ? "0" :
                    FormatModeDetail(s.TransLocalCount, s.TransLocalFrCount,
                        s.AvgTransLocalFrDurSec, s.TransLocalEnCount, s.AvgTransLocalEnDurSec);

                string remDetail = s.TransRemoteCount == 0 ? "0" :
                    FormatModeDetail(s.TransRemoteCount, s.TransRemoteFrCount,
                        s.AvgTransRemoteFrDurSec, s.TransRemoteEnCount, s.AvgTransRemoteEnDurSec);

                TxtStatsTransDetail.Text = $"Local: {locDetail}  ·  Remote: {remDetail}";
            }

            // ── Résumés ──────────────────────────────────────────────────────
            if (s.SummaryCount == 0)
            {
                TxtStatsSum.Text       = "0  (moy. — mots, durée —)";
                TxtStatsSumDetail.Text = "vers FR: 0 (—)  ·  vers EN: 0 (—)";
            }
            else
            {
                TxtStatsSum.Text =
                    $"{s.SummaryCount}  " +
                    $"(moy. {s.AvgSumWordCount:F0} mots, {FormatSec(s.AvgSumDurSec)})";

                string frStr = s.SumFrCount == 0
                    ? "0 (—)"
                    : $"{s.SumFrCount} ({FormatSec(s.AvgSumFrDurSec)})";
                string enStr = s.SumEnCount == 0
                    ? "0 (—)"
                    : $"{s.SumEnCount} ({FormatSec(s.AvgSumEnDurSec)})";

                TxtStatsSumDetail.Text = $"vers FR: {frStr}  ·  vers EN: {enStr}";
            }
        }

        private static string FormatModeDetail(int total,
            int frCount, double avgFrDur, int enCount, double avgEnDur)
        {
            string fr = frCount > 0 ? $"FR×{frCount}:{FormatSec(avgFrDur)}" : "";
            string en = enCount > 0 ? $"EN×{enCount}:{FormatSec(avgEnDur)}" : "";
            string inner = string.Join(" | ",
                new[] { fr, en }.Where(s => !string.IsNullOrEmpty(s)));
            return $"{total} ({inner})";
        }

        // ════════════════════════════════════════════════════════════════════
        //  HELPERS — UI & fichiers
        // ════════════════════════════════════════════════════════════════════
        private void ResetUI()
        {
            BtnStart.IsEnabled           = true;
            BtnStop.IsEnabled            = false;
            RbMicrophone.IsEnabled       = true;
            RbLoopback.IsEnabled         = true;
            RbBoth.IsEnabled             = true;
            TxtDuration.IsEnabled        = true;
            BtnOpenMp3.IsEnabled         = true;
            TxtStatus.Text               = "EN ATTENTE";
            TxtStatus.Foreground         = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xC0));
            bool hasFile = !string.IsNullOrEmpty(_lastSavedFilePath) && File.Exists(_lastSavedFilePath);
            BtnResume.IsEnabled          = hasFile;
            CbOutputLang.IsEnabled       = true;
            CbAudioLang.IsEnabled        = true;
            RbTranscribeLocal.IsEnabled  = true;
            RbTranscribeRemote.IsEnabled = true;
        }

        private void CleanupRecorder()
        {
            _timer.Stop();
            try { _captureDevice?.StopRecording();  } catch { }
            try { _captureDevice?.Dispose();         } catch { } _captureDevice  = null;
            try { _captureDevice2?.StopRecording(); } catch { }
            try { _captureDevice2?.Dispose();        } catch { } _captureDevice2 = null;
            try { _mp3Writer?.Dispose();             } catch { } _mp3Writer      = null;
            try { _wavWriter?.Dispose();             } catch { } _wavWriter      = null;
            try { _wavWriter2?.Dispose();            } catch { } _wavWriter2     = null;
            TryDeleteTemp();
            // Fichiers WAV conservés dans temp\ pour inspection
            VuMeter.Width  = 0;
            _smoothedLevel = 0;
            ResetUI();
        }

        private void TryDeleteTemp() => TryDeleteFile(_tempFilePath);

        private static void TryDeleteFile(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        // Dossier temporaire dans le projet (inspectiable facilement)
        private static readonly string _tempDir =
            Path.Combine(_projectDir, "temp");

        private static string TempPath(string prefix, string ext, string suffix = "")
        {
            Directory.CreateDirectory(_tempDir);
            string tag = string.IsNullOrEmpty(suffix) ? "" : $"_{suffix}";
            return Path.Combine(_tempDir,
                $"{prefix}{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
        }

        private static string TempMp3Path(string suffix = "")
            => TempPath("rec", "mp3", suffix);

        protected override void OnClosed(EventArgs e)
        {
            CleanupRecorder();
            base.OnClosed(e);
        }

        // ════════════════════════════════════════════════════════════════════
        //  HELPERS — texte, durée, tokens
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Formats acceptés :
        ///   "90"       → 90 minutes
        ///   "30:00"    → 30 min 00 s  (mm:ss)
        ///   "1:30:00"  → 1 h 30 min   (hh:mm:ss)
        /// TimeSpan.TryParse est volontairement évité : il interprète "1" comme
        /// 1 jour et "30:00" comme 30 heures.
        /// </summary>
        private static TimeSpan ParseTargetDuration(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return TimeSpan.Zero;
            var parts = input.Trim().Split(':');

            try
            {
                return parts.Length switch
                {
                    // Pas de deux-points → minutes (décimales acceptées)
                    1 when double.TryParse(parts[0],
                               System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out double m) && m > 0
                        => TimeSpan.FromMinutes(m),

                    // mm:ss
                    2 => TimeSpan.FromSeconds(
                             int.Parse(parts[0]) * 60 + int.Parse(parts[1])),

                    // hh:mm:ss
                    3 => TimeSpan.FromSeconds(
                             int.Parse(parts[0]) * 3600 +
                             int.Parse(parts[1]) * 60  +
                             int.Parse(parts[2])),

                    _ => TimeSpan.Zero
                };
            }
            catch { return TimeSpan.Zero; }
        }

        private static int CountWords(string text)
            => Regex.Matches(text, @"\b\w+\b").Count;

        private static string FormatDuration(double totalSec)
        {
            var ts = TimeSpan.FromSeconds(totalSec);
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }

        private static string FormatSec(double sec)
        {
            if (sec <= 0) return "—";
            if (sec < 60) return $"{sec:F0}s";
            return $"{(int)(sec / 60)}m{(int)(sec % 60):D2}s";
        }

        private static string WriteTextToFile(string text, string filePath)
        {
            string dir   = Path.GetDirectoryName(filePath)!;
            string name  = Path.GetFileNameWithoutExtension(filePath);
            string ext   = Path.GetExtension(filePath);
            string final = filePath;
            int    n     = 1;
            while (File.Exists(final))
                final = Path.Combine(dir, $"{name}_{n++}{ext}");
            File.WriteAllText(final, text, Encoding.UTF8);
            return final;
        }

        private static int EstimateTokens(string text)
            => (int)(CountWords(text) * 1.33);

        private static int CalculateMaxTokens(int inputTokens)
            => Math.Max(50, Math.Min((int)(inputTokens * 1.5), 5000));

        /// <summary>Taille du fichier en octets, -1 si absent ou erreur.</summary>
        private static long FileSizeBytes(string path)
        {
            try { return string.IsNullOrEmpty(path) ? -1 : new FileInfo(path).Length; }
            catch { return -1; }
        }
    }
}
