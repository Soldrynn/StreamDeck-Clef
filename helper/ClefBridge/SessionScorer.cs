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
        var process = Normalize(candidate.ProcessName);
        var path = Normalize(candidate.ExecutablePath);
        var identifier = Normalize(candidate.SessionIdentifier);
        var display = StripNumericPrefix(candidate.DisplayName);
        var score = 0;
        string? kind = null;

        if (process is "applemusic" or "applemusicexe")
        {
            score += 150;
            kind = "apple-music-process";
        }
        else if (process is "amplibraryagent" or "amplibraryagentexe")
        {
            score += 135;
            kind = "amp-agent-process";
        }

        if (path.Contains("applemusicexe") || identifier.Contains("applemusicexe"))
        {
            score += 90;
            kind ??= "apple-music-process";
        }
        if (path.Contains("amplibraryagentexe") || identifier.Contains("amplibraryagentexe"))
        {
            score += 85;
            kind ??= "amp-agent-process";
        }
        if (path.Contains("appleincapplemusic") || identifier.Contains("appleincapplemusic")) score += 25;

        if (display == "amplibraryagent")
        {
            score += 75;
            kind ??= "amp-agent-alias";
        }
        else if (display.Contains("applemusic"))
        {
            score += 65;
            kind ??= "apple-music-process";
        }

        if (candidate.State == AudioSessionState.Active) score += 20;
        if (candidate.State == AudioSessionState.Expired) score -= 100;
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
        Require(!SessionScorer.IsAcceptableAudioScore(SessionScorer.ScoreAudio(new("chrome", null, null, "YouTube", AudioSessionState.Active)).Score), "unrelated audio session");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }
}
