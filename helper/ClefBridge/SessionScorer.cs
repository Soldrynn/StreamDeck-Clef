using System.Text.RegularExpressions;

namespace ClefBridge;

internal static partial class SessionScorer
{
    private const float AudiblePeakThreshold = 0.0001f;

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

    public static bool IsAudiblePeak(float peak) => float.IsFinite(peak) && peak >= AudiblePeakThreshold;

    public static int CompareAudioCandidates(AudioCandidateRanking left, AudioCandidateRanking right)
    {
        var leftTier = AudioActivityTier(left);
        var rightTier = AudioActivityTier(right);
        var comparison = leftTier.CompareTo(rightTier);
        if (comparison != 0) return comparison;

        if (leftTier == 3)
        {
            comparison = left.Peak.CompareTo(right.Peak);
            if (comparison != 0) return comparison;
        }

        // Once a session has produced sound, keep the current binding stable
        // through quiet passages and pauses. Before activity is known, the
        // current/default render endpoint is a better tie-break than COM order.
        if (leftTier >= 2)
        {
            comparison = left.IsSelected.CompareTo(right.IsSelected);
            if (comparison != 0) return comparison;
        }
        comparison = left.IsDefaultEndpoint.CompareTo(right.IsDefaultEndpoint);
        if (comparison != 0) return comparison;
        comparison = left.IsSelected.CompareTo(right.IsSelected);
        if (comparison != 0) return comparison;
        return left.IdentityScore.CompareTo(right.IdentityScore);
    }

    internal static int AudioActivityTier(AudioCandidateRanking candidate)
    {
        if (IsAudiblePeak(candidate.Peak)) return 3;
        if (candidate.WasAudible) return 2;
        return candidate.State == AudioSessionState.Active ? 1 : 0;
    }

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
        Require(UiAutomationResolver.RepeatModeFromName("Repeat All") == "all", "repeat all name");
        Require(UiAutomationResolver.RepeatModeFromName("Repeat One") == "one", "repeat one name");
        Require(UiAutomationResolver.RepeatModeFromName("Do Not Repeat") == "off", "repeat off name");
        Require(UiAutomationResolver.RepeatModeFromName("Wiederholen") == "unknown", "unlocalized repeat name");
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

        var inactiveIdentityWinner = new AudioCandidateRanking(395, AudioSessionState.Inactive, 0, false, false, false);
        var activeStream = new AudioCandidateRanking(320, AudioSessionState.Active, 0.15f, false, false, false);
        Require(SessionScorer.CompareAudioCandidates(activeStream, inactiveIdentityWinner) > 0,
            "audible session beats stronger inactive identity");

        var rememberedStream = activeStream with { Peak = 0, State = AudioSessionState.Inactive, WasAudible = true, IsSelected = true };
        var newActiveDuplicate = inactiveIdentityWinner with { State = AudioSessionState.Active };
        Require(SessionScorer.CompareAudioCandidates(rememberedStream, newActiveDuplicate) > 0,
            "known audible session remains stable while quiet");

        var defaultActive = newActiveDuplicate with { IdentityScore = 200, IsDefaultEndpoint = true };
        var selectedActiveDuplicate = newActiveDuplicate with { IsSelected = true };
        Require(SessionScorer.CompareAudioCandidates(defaultActive, selectedActiveDuplicate) > 0,
            "default endpoint breaks an activity tie before stale selection");

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
