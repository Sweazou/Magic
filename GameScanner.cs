using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Playnite.SDK;

namespace Magic
{
    public static class GameScanner
    {
        private const long MediumCandidateSizeBytes = 50L * 1024 * 1024;
        private const long LargeCandidateSizeBytes = 150L * 1024 * 1024;
        private const int MinimumAcceptedScore = 30;

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Regex[] BadExecutablePatterns =
        {
            new Regex(@"(?:^|[^a-z0-9])uninstall(?:er)?(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|[^a-z0-9])setup(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|[^a-z0-9])launcher(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|[^a-z0-9])updater(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^unitycrashhandler32$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"^unitycrashhandler64$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"unitycrashhandler(?:32|64)?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|[^a-z0-9])(crash|report)(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"crash(?:report|reporter|handler|send|pad)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"report(?:crash|error|tool|client)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"dxsetup", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"redistributable", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        private static readonly string[] PreferredPathKeywords =
        {
            "binaries",
            "win64",
            "x64"
        };

        private static readonly string[] TechnicalDirectoryNames =
        {
            "game",
            "games",
            "binaries",
            "binary",
            "bin",
            "win64",
            "win32",
            "x64",
            "x86",
            "windowsnoeditor",
            "shipping",
            "release",
            "build",
            "engine",
            "app",
            "apps",
            "package",
            "packages",
            "test",
            "tests",
            "demo",
            "sample",
            "steam",
            "gog",
            "epic"
        };

        private static readonly Regex[] GameNameCleanupPatterns =
        {
            new Regex(@"\s*[\(\[\-_\s]*(goty(?:\s+edition)?)[\)\]]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s*[\(\[\-_\s]*(deluxe\s+edition)[\)\]]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s*[\(\[\-_\s]*(ultimate\s+edition)[\)\]]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s*[\(\[\-_\s]*(steam|pc)[\)\]]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        public static GameScanResult ScanGames(IEnumerable<string> gamesFolders, CancellationToken cancelToken)
        {
            var result = new GameScanResult();
            var detectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var gamesFolder in GetConfiguredRoots(gamesFolders))
            {
                if (cancelToken.IsCancellationRequested)
                {
                    logger.Info("Magic game scan canceled before completion.");
                    break;
                }

                if (!Directory.Exists(gamesFolder))
                {
                    logger.Warn($"Magic skipped unavailable root: {gamesFolder}");
                    continue;
                }

                result.AvailableRoots.Add(gamesFolder);
                logger.Info($"Magic scanning root: {gamesFolder}");

                IEnumerable<string> gameDirectories;
                try
                {
                    gameDirectories = Directory.EnumerateDirectories(gamesFolder)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception exception)
                {
                    logger.Error(exception, $"Magic failed to enumerate game folders in root {gamesFolder}");
                    continue;
                }

                foreach (var gameDirectory in gameDirectories)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        logger.Info("Magic game scan canceled while scanning folders.");
                        break;
                    }

                    logger.Info($"Magic checking game folder: {gameDirectory}");

                    var stableDirectory = NormalizePath(gameDirectory);
                    var logicalGameDirectory = ResolveLogicalGameDirectory(stableDirectory);
                    if (!string.Equals(logicalGameDirectory, stableDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.Info($"Magic resolved wrapper folder '{stableDirectory}' to logical game folder '{logicalGameDirectory}'.");
                    }

                    var gameNameCandidates = ResolveGameNameCandidates(stableDirectory, logicalGameDirectory);
                    var folderName = gameNameCandidates[0];
                    var cleanedGameName = CleanGameName(folderName);

                    ExecutableMatch executableMatch;
                    try
                    {
                        executableMatch = FindBestExecutable(
                            logicalGameDirectory,
                            folderName,
                            cleanedGameName,
                            gameNameCandidates.Skip(1));
                    }
                    catch (Exception exception)
                    {
                        logger.Error(exception, $"Magic failed to inspect game folder {gameDirectory}");
                        continue;
                    }

                    if (executableMatch == null || string.IsNullOrWhiteSpace(executableMatch.ExecutablePath))
                    {
                        logger.Info($"Magic skipped folder because no executable was found: {gameDirectory}");
                        continue;
                    }

                    var detectedGame = new DetectedGame(
                        cleanedGameName,
                        logicalGameDirectory,
                        executableMatch.ExecutablePath,
                        stableDirectory.ToLowerInvariant());

                    if (detectedIds.Add(detectedGame.StableId))
                    {
                        result.DetectedGames.Add(detectedGame);
                        logger.Info($"Magic detected game '{detectedGame.GameName}' with executable '{detectedGame.ExecutablePath}' using selection '{executableMatch.MatchReason}' (score {executableMatch.Score}).");
                    }
                    else
                    {
                        logger.Warn($"Magic ignored duplicate detected game path: {detectedGame.GameDirectory}");
                    }
                }
            }

            return result;
        }

        private static ExecutableMatch FindBestExecutable(
            string gameDirectory,
            string folderName,
            string cleanedGameName,
            IEnumerable<string> alternateFolderNames)
        {
            var candidates = CollectExecutableCandidates(gameDirectory);
            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredCandidates = candidates
                .Where(candidate => !candidate.HasBadKeyword)
                .ToList();

            var scoringCandidates = preferredCandidates.Count > 0
                ? preferredCandidates
                : candidates;

            if (preferredCandidates.Count > 0 && preferredCandidates.Count != candidates.Count)
            {
                logger.Info($"Magic excluded {candidates.Count - preferredCandidates.Count} obvious utility executable candidate(s) from primary scoring in folder: {gameDirectory}");
            }

            var scoredCandidates = scoringCandidates
                .Select(candidate => ScoreCandidate(candidate, folderName, cleanedGameName, alternateFolderNames))
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Candidate.FileSizeBytes)
                .ThenBy(candidate => candidate.Candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var scoredCandidate in scoredCandidates)
            {
                logger.Info($"Magic candidate '{scoredCandidate.Candidate.FilePath}' scored {scoredCandidate.Score}: {scoredCandidate.ScoreSummary}");
            }

            var bestCandidate = scoredCandidates[0];
            var fallbackCandidate = candidates
                .OrderByDescending(candidate => candidate.FileSizeBytes)
                .ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .First();
            var fallbackScoredCandidate = scoredCandidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Candidate.FilePath, fallbackCandidate.FilePath, StringComparison.OrdinalIgnoreCase))
                ?? ScoreCandidate(fallbackCandidate, folderName, cleanedGameName, alternateFolderNames);

            if (bestCandidate.Score < MinimumAcceptedScore)
            {
                logger.Info($"Magic fell back to the largest executable because the best score was below {MinimumAcceptedScore}: {fallbackCandidate.FilePath}");
                return new ExecutableMatch(
                    fallbackCandidate.FilePath,
                    $"largest executable fallback because best score {bestCandidate.Score} was below {MinimumAcceptedScore}",
                    fallbackScoredCandidate.Score);
            }

            return new ExecutableMatch(
                bestCandidate.Candidate.FilePath,
                bestCandidate.ScoreSummary,
                bestCandidate.Score);
        }

        private static List<ExecutableCandidate> CollectExecutableCandidates(string gameDirectory)
        {
            var executablePaths = Directory.EnumerateFiles(gameDirectory, "*.exe", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (executablePaths.Count == 0)
            {
                return new List<ExecutableCandidate>();
            }

            var candidates = new List<ExecutableCandidate>();

            foreach (var executablePath in executablePaths)
            {
                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(executablePath);
                }
                catch (Exception exception)
                {
                    logger.Warn($"Magic failed to inspect executable candidate '{executablePath}': {exception.Message}");
                    continue;
                }

                var normalizedPath = NormalizePath(executablePath);
                var executableName = Path.GetFileNameWithoutExtension(executablePath) ?? string.Empty;

                candidates.Add(new ExecutableCandidate(
                    normalizedPath,
                    executableName,
                    NormalizeName(executableName),
                    fileInfo.Length,
                    HasBadKeyword(executableName)));
            }

            return candidates;
        }

        private static ScoredExecutableCandidate ScoreCandidate(
            ExecutableCandidate candidate,
            string folderName,
            string cleanedGameName,
            IEnumerable<string> alternateFolderNames)
        {
            var score = 0;
            var reasons = new List<string>();
            var normalizedFolderName = NormalizeName(folderName);
            var normalizedGameName = NormalizeName(cleanedGameName);
            var normalizedAlternateNames = (alternateFolderNames ?? Enumerable.Empty<string>())
                .Select(NormalizeName)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, normalizedFolderName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, normalizedGameName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(normalizedFolderName))
            {
                if (string.Equals(candidate.NormalizedFileName, normalizedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 80;
                    reasons.Add("exact folder-name match (+80)");
                }
                else if (candidate.NormalizedFileName.Contains(normalizedFolderName))
                {
                    score += 60;
                    reasons.Add("folder-name contains match (+60)");
                }
            }

            if (normalizedAlternateNames.Any(name =>
                    string.Equals(candidate.NormalizedFileName, name, StringComparison.OrdinalIgnoreCase)))
            {
                score += 50;
                reasons.Add("parent-folder exact match (+50)");
            }
            else if (normalizedAlternateNames.Any(name => candidate.NormalizedFileName.Contains(name)))
            {
                score += 30;
                reasons.Add("parent-folder contains match (+30)");
            }

            if (!string.IsNullOrWhiteSpace(normalizedGameName) &&
                !string.Equals(normalizedGameName, normalizedFolderName, StringComparison.OrdinalIgnoreCase) &&
                candidate.NormalizedFileName.Contains(normalizedGameName))
            {
                score += 40;
                reasons.Add("cleaned game-name contains match (+40)");
            }

            if (HasSameInitial(candidate.NormalizedFileName, normalizedFolderName, normalizedGameName, normalizedAlternateNames))
            {
                score += 35;
                reasons.Add("same first-letter match (+35)");
            }

            if (PreferredPathKeywords.Any(keyword => candidate.FilePath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                score += 50;
                reasons.Add("preferred binaries path (+50)");
            }

            if (candidate.FileSizeBytes >= LargeCandidateSizeBytes)
            {
                score += 60;
                reasons.Add("file size >= 150 MB (+60)");
            }
            else if (candidate.FileSizeBytes >= MediumCandidateSizeBytes)
            {
                score += 40;
                reasons.Add("file size >= 50 MB (+40)");
            }

            if (candidate.HasBadKeyword)
            {
                score -= 200;
                reasons.Add("bad keyword penalty (-200)");
            }

            if (reasons.Count == 0)
            {
                reasons.Add("no strong signals");
            }

            return new ScoredExecutableCandidate(candidate, score, string.Join(", ", reasons));
        }

        private static bool HasBadKeyword(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            return BadExecutablePatterns.Any(pattern => pattern.IsMatch(executableName));
        }

        private static string CleanGameName(string folderName)
        {
            var cleanedName = folderName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanedName))
            {
                return string.Empty;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var pattern in GameNameCleanupPatterns)
                {
                    var updatedName = pattern.Replace(cleanedName, string.Empty).Trim();
                    if (!string.Equals(updatedName, cleanedName, StringComparison.Ordinal))
                    {
                        cleanedName = updatedName;
                        changed = true;
                    }
                }
            }

            cleanedName = cleanedName
                .Trim()
                .TrimEnd('-', '_', ' ', '(', '[', '{');

            return string.IsNullOrWhiteSpace(cleanedName)
                ? folderName?.Trim() ?? string.Empty
                : cleanedName;
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ResolveLogicalGameDirectory(string initialGameDirectory)
        {
            var currentDirectory = NormalizePath(initialGameDirectory);

            while (true)
            {
                if (Directory.EnumerateFiles(currentDirectory, "*.exe", SearchOption.TopDirectoryOnly).Any())
                {
                    return currentDirectory;
                }

                var childDirectories = Directory.EnumerateDirectories(currentDirectory)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (childDirectories.Count != 1)
                {
                    return currentDirectory;
                }

                var onlyChildDirectory = NormalizePath(childDirectories[0]);
                var onlyChildName = Path.GetFileName(onlyChildDirectory) ?? string.Empty;
                if (IsTechnicalDirectoryName(onlyChildName))
                {
                    return currentDirectory;
                }

                currentDirectory = onlyChildDirectory;
            }
        }

        private static List<string> ResolveGameNameCandidates(string stableDirectory, string logicalGameDirectory)
        {
            var candidateNames = new List<string>();
            var currentDirectory = NormalizePath(logicalGameDirectory);
            var normalizedStableDirectory = NormalizePath(stableDirectory);

            while (true)
            {
                var candidateName = Path.GetFileName(currentDirectory) ?? string.Empty;
                if (!IsTechnicalDirectoryName(candidateName) && !string.IsNullOrWhiteSpace(candidateName))
                {
                    candidateNames.Add(candidateName);
                }

                if (string.Equals(currentDirectory, normalizedStableDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var parentDirectory = Directory.GetParent(currentDirectory)?.FullName;
                if (string.IsNullOrWhiteSpace(parentDirectory))
                {
                    break;
                }

                currentDirectory = NormalizePath(parentDirectory);
            }

            if (candidateNames.Count > 0)
            {
                return candidateNames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new List<string>
            {
                Path.GetFileName(logicalGameDirectory) ?? Path.GetFileName(stableDirectory) ?? string.Empty
            };
        }

        private static bool HasSameInitial(
            string executableName,
            string normalizedFolderName,
            string normalizedGameName,
            IReadOnlyCollection<string> normalizedAlternateNames)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            var executableInitial = executableName[0];
            if (!string.IsNullOrWhiteSpace(normalizedFolderName) && normalizedFolderName[0] == executableInitial)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(normalizedGameName) && normalizedGameName[0] == executableInitial)
            {
                return true;
            }

            return normalizedAlternateNames.Any(name => name[0] == executableInitial);
        }

        private static bool IsTechnicalDirectoryName(string directoryName)
        {
            var normalizedName = NormalizeName(directoryName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return false;
            }

            return TechnicalDirectoryNames.Any(technicalName =>
                string.Equals(normalizedName, technicalName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> GetConfiguredRoots(IEnumerable<string> gamesFolders)
        {
            return (gamesFolders ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public sealed class DetectedGame
    {
        public DetectedGame(string gameName, string gameDirectory, string executablePath, string stableId)
        {
            GameName = gameName;
            GameDirectory = gameDirectory;
            ExecutablePath = executablePath;
            StableId = stableId;
        }

        public string GameName { get; }

        public string GameDirectory { get; }

        public string ExecutablePath { get; }

        public string StableId { get; }
    }

    public sealed class GameScanResult
    {
        public List<string> AvailableRoots { get; } = new List<string>();

        public List<DetectedGame> DetectedGames { get; } = new List<DetectedGame>();
    }

    public sealed class ExecutableMatch
    {
        public ExecutableMatch(string executablePath, string matchReason, int score)
        {
            ExecutablePath = executablePath;
            MatchReason = matchReason;
            Score = score;
        }

        public string ExecutablePath { get; }

        public string MatchReason { get; }

        public int Score { get; }
    }

    internal sealed class ExecutableCandidate
    {
        public ExecutableCandidate(string filePath, string fileName, string normalizedFileName, long fileSizeBytes, bool hasBadKeyword)
        {
            FilePath = filePath;
            FileName = fileName;
            NormalizedFileName = normalizedFileName;
            FileSizeBytes = fileSizeBytes;
            HasBadKeyword = hasBadKeyword;
        }

        public string FilePath { get; }

        public string FileName { get; }

        public string NormalizedFileName { get; }

        public long FileSizeBytes { get; }

        public bool HasBadKeyword { get; }
    }

    internal sealed class ScoredExecutableCandidate
    {
        public ScoredExecutableCandidate(ExecutableCandidate candidate, int score, string scoreSummary)
        {
            Candidate = candidate;
            Score = score;
            ScoreSummary = scoreSummary;
        }

        public ExecutableCandidate Candidate { get; }

        public int Score { get; }

        public string ScoreSummary { get; }
    }
}
