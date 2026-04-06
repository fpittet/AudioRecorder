using System;
using System.Collections.Generic;
using System.IO;
using AudioRecorder;
using FluentAssertions;
using Xunit;

namespace AudioRecorder.Tests;

// ═══════════════════════════════════════════════════════════════════════════
//  Tests unitaires de ActivityLogger.ComputeStats()
//
//  Deux approches cohabitent dans ce fichier :
//
//  1. ComputeStatsDirectTests  — appelle la méthode statique interne
//     ComputeStats(IEnumerable<LogEntry>) directement, sans fichier.
//     → Tests purs, rapides, sans effet de bord.
//
//  2. ActivityLoggerIntegrationTests — instancie ActivityLogger sur un
//     fichier temporaire et vérifie Stats après chaque Log().
//     → Couvre aussi la persistance JSON et le rechargement.
// ═══════════════════════════════════════════════════════════════════════════


// ───────────────────────────────────────────────────────────────────────────
//  Helper : fabrique de LogEntry avec des valeurs par défaut sensées
// ───────────────────────────────────────────────────────────────────────────
file static class E
{
    public static LogEntry Recording(double durationSec = 60) => new()
    {
        Action      = "RecordingSaved",
        DurationSec = durationSec,
    };

    public static LogEntry Trans(
        string mode, string inputLang,
        double procDur = 10, int words = 100) => new()
    {
        Action           = "TranscriptionDone",
        Mode             = mode,
        InputLang        = inputLang,
        ProcessingDurSec = procDur,
        WordCount        = words,
    };

    public static LogEntry Summary(
        string outputLang,
        double procDur = 5, int words = 50) => new()
    {
        Action           = "SummaryDone",
        OutputLang       = outputLang,
        ProcessingDurSec = procDur,
        WordCount        = words,
    };

    public static LogEntry Other(string action) => new() { Action = action };
}


// ═══════════════════════════════════════════════════════════════════════════
//  1. Tests directs sur la méthode statique pure
// ═══════════════════════════════════════════════════════════════════════════
public class ComputeStatsDirectTests
{
    // Raccourci
    private static ActivityStats Compute(params LogEntry[] entries)
        => ActivityLogger.ComputeStats(entries);

    // ── État vide ────────────────────────────────────────────────────────
    [Fact]
    public void EmptyList_AllCountsAreZero()
    {
        var s = Compute();
        s.RecordingCount.Should().Be(0);
        s.TranscriptionCount.Should().Be(0);
        s.SummaryCount.Should().Be(0);
    }

    [Fact]
    public void EmptyList_AllAveragesAreZero()
    {
        var s = Compute();
        s.AvgDurationSec.Should().Be(0);
        s.AvgTransDurSec.Should().Be(0);
        s.AvgTransWordCount.Should().Be(0);
        s.AvgSumDurSec.Should().Be(0);
        s.AvgSumWordCount.Should().Be(0);
    }

    // ── Enregistrements ──────────────────────────────────────────────────
    [Fact]
    public void Recordings_CountAndAvgDuration()
    {
        var s = Compute(E.Recording(60), E.Recording(120));
        s.RecordingCount.Should().Be(2);
        s.AvgDurationSec.Should().Be(90);
    }

    [Fact]
    public void Recordings_SingleEntry_AvgEqualsValue()
    {
        var s = Compute(E.Recording(45));
        s.RecordingCount.Should().Be(1);
        s.AvgDurationSec.Should().Be(45);
    }

    [Fact]
    public void Recordings_OtherActionsNotCounted()
    {
        var s = Compute(
            E.Other("RecordingStarted"),
            E.Other("RecordingCancelled"),
            E.Other("RecordingError"));
        s.RecordingCount.Should().Be(0);
        s.AvgDurationSec.Should().Be(0);
    }

    // ── Transcriptions — comptages ───────────────────────────────────────
    [Fact]
    public void Trans_GlobalCount()
    {
        var s = Compute(E.Trans("Local", "fr"), E.Trans("Remote", "en"));
        s.TranscriptionCount.Should().Be(2);
    }

    [Fact]
    public void Trans_LocalVsRemoteCounts()
    {
        var s = Compute(
            E.Trans("Local",  "fr"),
            E.Trans("Local",  "en"),
            E.Trans("Remote", "fr"));
        s.TransLocalCount.Should().Be(2);
        s.TransRemoteCount.Should().Be(1);
    }

    [Fact]
    public void Trans_LocalFrAndEnBreakdown()
    {
        var s = Compute(
            E.Trans("Local", "fr", procDur: 8),
            E.Trans("Local", "fr", procDur: 12),
            E.Trans("Local", "en", procDur: 6));
        s.TransLocalFrCount.Should().Be(2);
        s.AvgTransLocalFrDurSec.Should().Be(10);
        s.TransLocalEnCount.Should().Be(1);
        s.AvgTransLocalEnDurSec.Should().Be(6);
    }

    [Fact]
    public void Trans_RemoteFrAndEnBreakdown()
    {
        var s = Compute(
            E.Trans("Remote", "en", procDur: 4),
            E.Trans("Remote", "en", procDur: 6));
        s.TransRemoteEnCount.Should().Be(2);
        s.AvgTransRemoteEnDurSec.Should().Be(5);
        s.TransRemoteFrCount.Should().Be(0);
        s.AvgTransRemoteFrDurSec.Should().Be(0);
    }

    [Fact]
    public void Trans_AbsentSubgroup_CountZeroAndAvgZero()
    {
        // Aucune transcription Remote → Remote doit rester à 0
        var s = Compute(E.Trans("Local", "fr"));
        s.TransRemoteCount.Should().Be(0);
        s.AvgTransRemoteDurSec.Should().Be(0);
        s.TransRemoteFrCount.Should().Be(0);
        s.TransRemoteEnCount.Should().Be(0);
    }

    // ── Transcriptions — moyennes ────────────────────────────────────────
    [Fact]
    public void Trans_AvgWordCountAndGlobalDuration()
    {
        var s = Compute(
            E.Trans("Local", "fr", procDur: 10, words: 200),
            E.Trans("Local", "fr", procDur: 20, words: 400));
        s.AvgTransWordCount.Should().Be(300);
        s.AvgTransDurSec.Should().Be(15);
    }

    [Fact]
    public void Trans_LocalAndRemoteAvgsAreIndependent()
    {
        var s = Compute(
            E.Trans("Local",  "fr", procDur: 10),
            E.Trans("Remote", "fr", procDur: 30));
        s.AvgTransLocalFrDurSec.Should().Be(10);
        s.AvgTransRemoteFrDurSec.Should().Be(30);
        // Moyenne globale mélange les deux
        s.AvgTransDurSec.Should().Be(20);
    }

    // ── Résumés ──────────────────────────────────────────────────────────
    [Fact]
    public void Summary_GlobalCount()
    {
        var s = Compute(E.Summary("français"), E.Summary("anglais"));
        s.SummaryCount.Should().Be(2);
    }

    [Fact]
    public void Summary_FrAndEnBreakdown()
    {
        var s = Compute(
            E.Summary("français", procDur: 5),
            E.Summary("français", procDur: 15),
            E.Summary("anglais",  procDur: 10));
        s.SumFrCount.Should().Be(2);
        s.AvgSumFrDurSec.Should().Be(10);
        s.SumEnCount.Should().Be(1);
        s.AvgSumEnDurSec.Should().Be(10);
    }

    [Fact]
    public void Summary_AvgWordCount()
    {
        var s = Compute(
            E.Summary("français", words: 100),
            E.Summary("anglais",  words: 200));
        s.AvgSumWordCount.Should().Be(150);
    }

    [Fact]
    public void Summary_AbsentLang_CountZeroAndAvgZero()
    {
        var s = Compute(E.Summary("français", procDur: 8));
        s.SumEnCount.Should().Be(0);
        s.AvgSumEnDurSec.Should().Be(0);
    }

    // ── Isolation des catégories ─────────────────────────────────────────
    [Fact]
    public void AllCategories_MixedEntries_NoLeakBetweenCounters()
    {
        var s = Compute(
            E.Recording(60),
            E.Trans("Local", "fr"),
            E.Summary("français"));
        s.RecordingCount.Should().Be(1);
        s.TranscriptionCount.Should().Be(1);
        s.SummaryCount.Should().Be(1);
        // Un Recording ne doit pas faire monter les compteurs Trans/Summary
        s.TransLocalFrCount.Should().Be(1);
        s.SumFrCount.Should().Be(1);
    }

    // ── Scénario complet ─────────────────────────────────────────────────
    [Fact]
    public void FullScenario_AllCountsAndAveragesCorrect()
    {
        var s = Compute(
            // 2 enregistrements
            E.Recording(60), E.Recording(120),
            // 3 transcriptions : Local FR×1 + Local EN×1 + Remote FR×1
            E.Trans("Local",  "fr", procDur: 8,  words: 300),
            E.Trans("Local",  "en", procDur: 12, words: 150),
            E.Trans("Remote", "fr", procDur: 25, words: 450),
            // 2 résumés : FR×1 + EN×1
            E.Summary("français", procDur: 5,  words: 80),
            E.Summary("anglais",  procDur: 10, words: 60));

        // Enregistrements
        s.RecordingCount.Should().Be(2);
        s.AvgDurationSec.Should().Be(90);

        // Transcriptions globales
        s.TranscriptionCount.Should().Be(3);
        s.AvgTransWordCount.Should().BeApproximately(300, 1);
        s.AvgTransDurSec.Should().BeApproximately(15, 0.01);

        // Transcriptions Local
        s.TransLocalCount.Should().Be(2);
        s.TransLocalFrCount.Should().Be(1);
        s.AvgTransLocalFrDurSec.Should().Be(8);
        s.TransLocalEnCount.Should().Be(1);
        s.AvgTransLocalEnDurSec.Should().Be(12);

        // Transcriptions Remote
        s.TransRemoteCount.Should().Be(1);
        s.TransRemoteFrCount.Should().Be(1);
        s.AvgTransRemoteFrDurSec.Should().Be(25);
        s.TransRemoteEnCount.Should().Be(0);

        // Résumés
        s.SummaryCount.Should().Be(2);
        s.AvgSumWordCount.Should().Be(70);
        s.SumFrCount.Should().Be(1);
        s.AvgSumFrDurSec.Should().Be(5);
        s.SumEnCount.Should().Be(1);
        s.AvgSumEnDurSec.Should().Be(10);
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  2. Tests d'intégration via ActivityLogger (fichier JSON réel)
//     Couvrent : Log() → Save() → ComputeStats() → Stats
//     et le rechargement depuis disque.
// ═══════════════════════════════════════════════════════════════════════════
public class ActivityLoggerIntegrationTests : IDisposable
{
    private readonly string         _tmpPath;
    private readonly ActivityLogger _logger;

    public ActivityLoggerIntegrationTests()
    {
        // Fichier temporaire vierge (Load() silencieux si absent)
        _tmpPath = Path.Combine(Path.GetTempPath(), $"test_log_{Guid.NewGuid():N}.json");
        _logger  = new ActivityLogger(_tmpPath);
    }

    public void Dispose()
    {
        try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private void Recording(double dur = 60)
        => _logger.Log("RecordingSaved", durationSec: dur);

    private void Trans(string mode, string lang, double procDur = 10, int words = 100)
        => _logger.Log("TranscriptionDone", mode: mode, inputLang: lang,
                        processingDurSec: procDur, wordCount: words);

    private void Summary(string lang, double procDur = 5, int words = 50)
        => _logger.Log("SummaryDone", outputLang: lang,
                        processingDurSec: procDur, wordCount: words);

    // ── Stats mises à jour en temps réel après chaque Log() ──────────────
    [Fact]
    public void Stats_UpdatedAfterEachLog()
    {
        _logger.Stats.RecordingCount.Should().Be(0);
        Recording();
        _logger.Stats.RecordingCount.Should().Be(1);
        Recording();
        _logger.Stats.RecordingCount.Should().Be(2);
    }

    [Fact]
    public void Stats_TranscriptionCount_AfterTwoLogs()
    {
        Trans("Local",  "fr");
        Trans("Remote", "en");
        _logger.Stats.TranscriptionCount.Should().Be(2);
        _logger.Stats.TransLocalCount.Should().Be(1);
        _logger.Stats.TransRemoteCount.Should().Be(1);
    }

    // ── Persistance : rechargement depuis JSON ────────────────────────────
    [Fact]
    public void Stats_SurviveJsonRoundTrip()
    {
        Recording(90);
        Trans("Local", "fr", procDur: 10, words: 200);
        Summary("français", procDur: 6, words: 80);

        // Recharger depuis le fichier JSON écrit par Save()
        var reloaded = new ActivityLogger(_tmpPath);

        reloaded.Stats.RecordingCount.Should().Be(1);
        reloaded.Stats.AvgDurationSec.Should().Be(90);
        reloaded.Stats.TranscriptionCount.Should().Be(1);
        reloaded.Stats.TransLocalFrCount.Should().Be(1);
        reloaded.Stats.AvgTransLocalFrDurSec.Should().Be(10);
        reloaded.Stats.SummaryCount.Should().Be(1);
        reloaded.Stats.SumFrCount.Should().Be(1);
        reloaded.Stats.AvgSumFrDurSec.Should().Be(6);
    }

    [Fact]
    public void Stats_MultipleReloads_StableResult()
    {
        Trans("Remote", "en", procDur: 20);

        var r1 = new ActivityLogger(_tmpPath);
        var r2 = new ActivityLogger(_tmpPath);

        r1.Stats.TransRemoteEnCount.Should().Be(1);
        r2.Stats.TransRemoteEnCount.Should().Be(1);
        r1.Stats.AvgTransRemoteEnDurSec.Should().Be(r2.Stats.AvgTransRemoteEnDurSec);
    }

    // ── Entrées ignorées ne polluent pas les stats ────────────────────────
    [Fact]
    public void Stats_DebugEntries_NotCounted()
    {
        _logger.Log("DbgStop_Start");
        _logger.Log("DbgFinish_MixDone");
        _logger.Log("TranscriptionStarted");

        _logger.Stats.RecordingCount.Should().Be(0);
        _logger.Stats.TranscriptionCount.Should().Be(0);
        _logger.Stats.SummaryCount.Should().Be(0);
    }
}
