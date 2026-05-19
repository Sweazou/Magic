using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Magic
{
    [LoadPlugin]
    public class MagicPlugin : LibraryPlugin
    {
        private const string PluginName = "Magic";
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly MagicSettings settings;

        public override Guid Id { get; } = Guid.Parse("6be4d8fa-b4cc-4a6c-97ea-6850c09dc5b4");
        public override string Name => PluginName;
        public override string LibraryIcon => Path.Combine(
            Path.GetDirectoryName(typeof(MagicPlugin).Assembly.Location) ?? string.Empty,
            "icon.png");

        public MagicPlugin(IPlayniteAPI api) : base(api)
        {
            settings = new MagicSettings(this);
            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new MagicSettingsView();
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            try
            {
                var configuredRoots = settings.GetGamesFolders();
                if (configuredRoots.Count == 0)
                {
                    logger.Info("Magic library scan skipped because no games folders are configured.");
                    return Enumerable.Empty<GameMetadata>();
                }

                logger.Info($"Magic library scan started with {configuredRoots.Count} configured root(s).");
                var scanResult = GameScanner.ScanGames(configuredRoots, args.CancelToken);
                if (scanResult.AvailableRoots.Count == 0)
                {
                    logger.Warn("Magic library scan skipped because none of the configured roots are currently available.");
                    return Enumerable.Empty<GameMetadata>();
                }

                var detectedGames = scanResult.DetectedGames;
                var detectedIds = new HashSet<string>(
                    detectedGames.Select(game => game.StableId),
                    StringComparer.OrdinalIgnoreCase);

                var metadata = detectedGames
                    .Select(CreateInstalledMetadata)
                    .ToList();

                var markedMissingCount = 0;

                foreach (var existingGame in PlayniteApi.Database.Games
                             .Where(game => game.PluginId == Id && !string.IsNullOrWhiteSpace(game.GameId)))
                {
                    if (!detectedIds.Contains(existingGame.GameId) &&
                        IsUnderAnyRoot(existingGame.InstallDirectory, scanResult.AvailableRoots))
                    {
                        metadata.Add(CreateMissingMetadata(existingGame));
                        markedMissingCount++;
                        logger.Info($"Magic marked game as uninstalled because it was not found during scan: {existingGame.Name} ({existingGame.InstallDirectory})");
                    }
                }

                logger.Info($"Magic library scan completed. Available roots: {scanResult.AvailableRoots.Count}, detected games: {detectedGames.Count}, marked missing: {markedMissingCount}.");
                return metadata;
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Magic library scan failed.");
                return Enumerable.Empty<GameMetadata>();
            }
        }

        private GameMetadata CreateInstalledMetadata(DetectedGame detectedGame)
        {
            var executableDirectory = Path.GetDirectoryName(detectedGame.ExecutablePath) ?? detectedGame.GameDirectory;

            return new GameMetadata
            {
                Name = detectedGame.GameName,
                GameId = detectedGame.StableId,
                InstallDirectory = detectedGame.GameDirectory,
                IsInstalled = true,
                Hidden = false,
                Source = new MetadataNameProperty(PluginName),
                Icon = new MetadataFile(detectedGame.ExecutablePath),
                GameActions = new List<GameAction>
                {
                    new GameAction
                    {
                        Type = GameActionType.File,
                        Path = detectedGame.ExecutablePath,
                        WorkingDir = executableDirectory,
                        IsPlayAction = true,
                        TrackingMode = TrackingMode.Directory,
                        TrackingPath = detectedGame.GameDirectory
                    }
                }
            };
        }

        private static bool IsUnderAnyRoot(string path, IReadOnlyCollection<string> availableRoots)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath = NormalizePath(path);
            }
            catch
            {
                return false;
            }

            return availableRoots.Any(root =>
                string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private GameMetadata CreateMissingMetadata(Game existingGame)
        {
            return new GameMetadata
            {
                Name = existingGame.Name,
                GameId = existingGame.GameId,
                IsInstalled = false,
                Hidden = true,
                Source = new MetadataNameProperty(PluginName),
                GameActions = new List<GameAction>()
            };
        }
    }
}
