using System;
using System.IO;
using System.Text.Json;

namespace AudioRecorder
{
    /// <summary>
    /// Charge la configuration depuis appsettings.json.
    /// Recherche le fichier dans le dossier projet (dev) ou à côté de l'exe (publié).
    /// Le fichier appsettings.json est exclu du dépôt Git (.gitignore).
    /// Utiliser appsettings.example.json comme modèle.
    /// </summary>
    internal static class AppSettings
    {
        public static string InfApiToken   { get; }
        public static int    InfProductId  { get; }
        public static string TranscribeUrl { get; }

        static AppSettings()
        {
            string path = FindSettingsPath();

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fichier de configuration manquant : {path}\n" +
                    "Copier appsettings.example.json → appsettings.json et renseigner les valeurs.",
                    path);

            try
            {
                using var doc  = JsonDocument.Parse(File.ReadAllText(path));
                var        root = doc.RootElement;
                InfApiToken   = root.GetProperty("InfApiToken").GetString()   ?? "";
                InfProductId  = root.GetProperty("InfProductId").GetInt32();
                TranscribeUrl = root.GetProperty("TranscribeUrl").GetString() ?? "";
            }
            catch (Exception ex) when (ex is not FileNotFoundException)
            {
                throw new InvalidOperationException(
                    $"Erreur de lecture de {path} : {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Remonte depuis le dossier de l'exe jusqu'à trouver un *.csproj
        /// (mode développement) ou retourne le dossier de l'exe (mode publié).
        /// </summary>
        private static string FindSettingsPath()
        {
            var dir   = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            int depth = 0;
            while (dir != null && dir.GetFiles("*.csproj").Length == 0 && depth < 5)
            {
                dir = dir.Parent;
                depth++;
            }

            string baseDir = (dir != null && dir.GetFiles("*.csproj").Length > 0)
                             ? dir.FullName
                             : AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(baseDir, "appsettings.json");
        }
    }
}
