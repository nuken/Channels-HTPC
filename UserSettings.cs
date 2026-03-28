using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FeralCode
{
    // --- NEW: The Data Model for your Custom App Links ---
    public class ExternalStream
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string Service { get; set; } = "Netflix";
        public string StreamId { get; set; } = "";
    }

    public class UserSettings
    {
        public bool AutoSkipCommercials { get; set; } = true;
        public bool IsLightTheme { get; set; } = false; 
        public string LastServerAddress { get; set; } = ""; 
        public string LastCollection { get; set; } = "All Channels";
		public bool StartPlayersFullscreen { get; set; } = false;
		public bool EnableTimeShiftBuffer { get; set; } = false;
		public int GuideDurationHours { get; set; } = 4;
        public int WebServerPort { get; set; } = 0;
        public bool StickyGuideHeaders { get; set; } = true;
		public bool ShowExtendedMetadata { get; set; } = false;
		public bool ForceAacAudio { get; set; } = true;
        public List<ExternalStream> ExternalStreams { get; set; } = new List<ExternalStream>();
		public bool EnableDebugLogging { get; set; } = false;
    }

   public static class SettingsManager
    {
        // --- NEW: Route the settings file to the writable Windows AppData folder ---
        private static string GetFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folderPath = Path.Combine(appData, "FeralHTPC");
            
            // Create the FeralHTPC folder in AppData if it doesn't exist yet
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            
            return Path.Combine(folderPath, "user_settings.json");
        }

        public static UserSettings Load()
        {
            string filePath = GetFilePath();
            if (File.Exists(filePath))
            {
                try 
                { 
                    return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(filePath)) ?? new UserSettings(); 
                }
                catch { return new UserSettings(); }
            }
            return new UserSettings();
        }

        public static void Save(UserSettings settings)
        {
            string filePath = GetFilePath();
            File.WriteAllText(filePath, JsonSerializer.Serialize(settings));
        }
    }

}