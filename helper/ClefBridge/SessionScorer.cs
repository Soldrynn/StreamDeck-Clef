using System.Text.RegularExpressions;

namespace ClefBridge;

internal static partial class SessionScorer
{
    public static int ScoreMedia(string? sourceAppId, bool isCurrent, bool isPlaying)
    {
        var normalized = Normalize(sourceAppId);
        var score = 0;
        if (normalized.Contains("applemusic")) score += 130;
        if (normalized.Contains("appleinc")) score += 15;
        if (normalized.EndsWith("applemusicexe")) score += 20;
        if (isCurrent) score += 15;
        if (isPlaying) score += 10;
        return score;
    }

    public static (int Score, string? BindingKind) ScoreAudio(AudioCandidateEvidence candidate)
    {
        // Expired sessions can remain enumerable after Apple Music replaces its
        // audio stream. Never let strong process-name evidence keep one bound.
        if (candidate.State == AudioSessionState.Expired) return (int.MinValue, null);

        var process = Normalize(candidate.ProcessName);
        var path = Normalize(candidate.ExecutablePath);
        var identifier = Normalize(candidate.SessionIdentifier);
        var display = StripNumericPrefix(candidate.DisplayName);
        var ampProcessName = process is "amplibraryagent" or "amplibraryagentexe";
        var ampPath = path.Contains("amplibraryagent");
        var ampIdentifier = identifier.Contains("amplibraryagent");
        var ampAlias = display == "amplibraryagent";
        var hasAmpEvidence = ampProcessName || ampPath || ampIdentifier || ampAlias;
        var score = 0;
        string? kind = null;

        // AppleMusic.exe owns the media session, but current Apple Music for
        // Windows builds send audio through AmpLibraryAgent.exe. Treat the
        // frontend/package identity only as corroborating evidence: binding to
        // it makes volume appear connected while changing the wrong session.
        if (ampProcessName)
        {
            score += 160;
            kind = "amp-agent-process";
        }
        if (ampPath)
        {
            score += 120;
            kind = "amp-agent-process";
        }
        if (ampIdentifier)
        {
            score += 100;
            kind = "amp-agent-process";
        }
        if (ampAlias)
        {
            score += 90;
            kind ??= "amp-agent-alias";
        }
        if (hasAmpEvidence && (path.Contains("appleincapplemusic") || identifier.Contains("appleincapplemusic")))
            score += 15;

        if (candidate.State == AudioSessionState.Active) score += 25;
        return (score, kind);
    }

    public static bool IsAcceptableAudioScore(int score) => score >= 70;

    internal static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NonAlphaNumeric().Replace(value.ToLowerInvariant(), string.Empty);

    internal static string StripNumericPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var withoutPrefix = NumericPrefix().Replace(value.Trim(), string.Empty);
        return Normalize(withoutPrefix);
    }

    [GeneratedRegex("[^a-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();

    [GeneratedRegex("^\\s*\\d+\\s*[-_:]\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPrefix();
}

internal static class ResolverSelfTests
{
    public static void Run()
    {
        CoreAudioCallbackSelfTests.Run();
        MemorySafetySelfTests.Run();
        ArtworkAssociationSelfTests.Run();
        Require(SessionScorer.ScoreMedia("AppleMusic.exe", false, false) >= 100, "Apple Music for Windows media identity");
        Require(SessionScorer.ScoreMedia("Spotify.exe", true, true) < 70, "unrelated current media session");

        foreach (var alias in new[] { "Amp Library Agent", "2-Amp Library Agent", "12 - Amp Library Agent", "3_Amp Library Agent" })
        {
            var result = SessionScorer.ScoreAudio(new(null, null, null, alias, AudioSessionState.Active));
            Require(SessionScorer.IsAcceptableAudioScore(result.Score), $"tolerant alias {alias}");
            Require(result.BindingKind == "amp-agent-alias", $"alias binding kind {alias}");
        }

        var processResult = SessionScorer.ScoreAudio(new("AmpLibraryAgent", @"C:\\Program Files\\WindowsApps\\AppleInc.AppleMusic_1.0\\AmpLibraryAgent.exe", null, "7-Amp Library Agent", AudioSessionState.Inactive));
        Require(processResult.Score > 200, "stable process/path evidence");

        var expiredResult = SessionScorer.ScoreAudio(new("AmpLibraryAgent", null, null, "Amp Library Agent", AudioSessionState.Expired));
        Require(!SessionScorer.IsAcceptableAudioScore(expiredResult.Score), "expired Amp session is rejected");

        var frontEndResult = SessionScorer.ScoreAudio(new(
            "AppleMusic",
            @"C:\\Program Files\\WindowsApps\\AppleInc.AppleMusic_1.0\\AppleMusic.exe",
            "AppleInc.AppleMusic_1.0!AppleMusic.exe",
            "Apple Music",
            AudioSessionState.Active));
        Require(!SessionScorer.IsAcceptableAudioScore(frontEndResult.Score), "Apple Music frontend is not an audio target");
        Require(frontEndResult.BindingKind is null, "Apple Music frontend has no audio binding kind");

        Require(!SessionScorer.IsAcceptableAudioScore(SessionScorer.ScoreAudio(new("chrome", null, null, "YouTube", AudioSessionState.Active)).Score), "unrelated audio session");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }
}
