using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Playnite.SDK;

namespace Magic
{
    public class MagicSettings : ISettings, INotifyPropertyChanged
    {
        private readonly MagicPlugin plugin;
        private string gamesFoldersText = string.Empty;
        private string editingBackup = string.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        public string GamesFoldersText
        {
            get => gamesFoldersText;
            set => SetField(ref gamesFoldersText, value);
        }

        // Backward-compatible alias for older single-folder settings.
        public string GamesFolder
        {
            get => gamesFoldersText;
            set => GamesFoldersText = value;
        }

        public MagicSettings()
        {
        }

        public MagicSettings(MagicPlugin plugin)
        {
            this.plugin = plugin;

            var savedSettings = plugin.LoadPluginSettings<MagicSettings>();
            if (savedSettings != null)
            {
                GamesFoldersText = !string.IsNullOrWhiteSpace(savedSettings.GamesFoldersText)
                    ? savedSettings.GamesFoldersText
                    : savedSettings.GamesFolder;
            }
        }

        public List<string> GetGamesFolders()
        {
            return (GamesFoldersText ?? string.Empty)
                .Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Save()
        {
            plugin?.SavePluginSettings(this);
        }

        public void BeginEdit()
        {
            editingBackup = GamesFoldersText ?? string.Empty;
        }

        public void CancelEdit()
        {
            GamesFoldersText = editingBackup;
        }

        public void EndEdit()
        {
            Save();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            foreach (var path in GetGamesFolders())
            {
                if (!Path.IsPathRooted(path))
                {
                    errors.Add($"Games folder must be an absolute path: {path}");
                }
            }

            return errors.Count == 0;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
