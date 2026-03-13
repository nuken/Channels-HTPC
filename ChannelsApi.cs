#pragma warning disable CS8602
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Linq;
using Zeroconf;

namespace ChannelsNativeTest
{
    public class DvrServer
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; } = 8089;
        public string Name { get; set; } = "Channels DVR";
        public string BaseUrl => $"http://{Ip}:{Port}";
        public override string ToString() => $"{Name} ({Ip}:{Port})";
    }

    public class ChannelsApi
    {
        private readonly HttpClient _http = new HttpClient();

        public async Task<List<DvrServer>> DiscoverDvrServersAsync()
        {
            var servers = new List<DvrServer>();
            try
            {
                // Discover all broadcasted DVRs on the local network
                IReadOnlyList<IZeroconfHost> results = await ZeroconfResolver.ResolveAsync("_channels_dvr._tcp.local.");
                foreach (var host in results)
                {
                    int port = 8089;
                    // Extract the specific port from the Zeroconf service payload
                    if (host.Services.TryGetValue("_channels_dvr._tcp.local.", out var service))
                    {
                        port = service.Port;
                    }

                    servers.Add(new DvrServer 
                    { 
                        Ip = host.IPAddress, 
                        Port = port, 
                        Name = host.DisplayName ?? "Channels DVR Server" 
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Zeroconf discovery failed: {ex.Message}");
            }
            return servers;
        }
		
		public async Task<List<TvShow>> GetShowsAsync(string baseUrl)
        {
            try {
                // FIXED: Changed _httpClient to _http
                var json = await _http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/v1/shows");
                return System.Text.Json.JsonSerializer.Deserialize<List<TvShow>>(json) ?? new List<TvShow>();
            } catch { return new List<TvShow>(); }
        }

        public async Task<List<Episode>> GetEpisodesAsync(string baseUrl)
        {
            try {
                // FIXED: Changed _httpClient to _http
                var json = await _http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/v1/episodes");
                return System.Text.Json.JsonSerializer.Deserialize<List<Episode>>(json) ?? new List<Episode>();
            } catch { return new List<Episode>(); }
        }

        public async Task<List<Channel>> GetChannelsAsync(string baseUrl)
        {
            var url = $"{baseUrl}/devices/ANY/channels";
            var response = await _http.GetStringAsync(url);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            return JsonSerializer.Deserialize<List<Channel>>(response, options) ?? new List<Channel>();
        }
        
        public async Task<List<ChannelCollection>> GetChannelCollectionsAsync(string baseUrl)
        {
            try
            {
                string url = $"{baseUrl}/dvr/collections/channels";
                var response = await _http.GetStringAsync(url);
                var collections = JsonSerializer.Deserialize<List<ChannelCollection>>(response, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                return collections ?? new List<ChannelCollection>();
            }
            catch
            {
                return new List<ChannelCollection>();
            }
        }

        public async Task<List<GuideData>> GetGuideAsync(string baseUrl)
        {
            long unixTime = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds();
            var url = $"{baseUrl}/devices/ANY/guide?time={unixTime}&duration=14400";
            var response = await _http.GetStringAsync(url);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            
            return JsonSerializer.Deserialize<List<GuideData>>(response, options) ?? new List<GuideData>();
        }
		
		// --- NEW: Fetch Recorded Movies from the DVR ---
        public async Task<List<Movie>> GetMoviesAsync(string baseUrl)
        {
            try
            {
                // Self-contained HTTP Client and JSON Options!
                using var client = new System.Net.Http.HttpClient();
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // Connecting to the exact endpoint from your JSON sample
                string url = $"{baseUrl.TrimEnd('/')}/api/v1/movies";
                var response = await client.GetStringAsync(url);
                
                var movies = System.Text.Json.JsonSerializer.Deserialize<List<Movie>>(response, options) ?? new List<Movie>();

                // Ensure the Poster URLs are absolute so WPF can render them
                foreach (var movie in movies)
                {
                    if (!string.IsNullOrWhiteSpace(movie.RawImage))
                    {
                        if (movie.RawImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)) 
                            movie.PosterUrl = movie.RawImage;
                        else if (movie.RawImage.StartsWith("/")) 
                            movie.PosterUrl = $"{baseUrl.TrimEnd('/')}{movie.RawImage}";
                        else 
                            movie.PosterUrl = $"{baseUrl.TrimEnd('/')}/{movie.RawImage}";
                    }
                }

                // Sort the library alphabetically by Title
                return movies.OrderBy(m => m.Title).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch movies: {ex.Message}");
                return new List<Movie>();
            }
        }

        public async Task<List<Station>> GetStationsAsync(string baseUrl)
        {
            try
            {
                var url = $"{baseUrl}/dvr/guide/stations";
                var response = await _http.GetStringAsync(url);
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                return JsonSerializer.Deserialize<List<Station>>(response, options) ?? new List<Station>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load stations: {ex.Message}");
                return new List<Station>();
            }
        }
    }

    public class Channel
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtraData { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("___id")]
        public string Id => GetValue("id");

        [System.Text.Json.Serialization.JsonPropertyName("___number")]
        public string Number => GetValue("number", "GuideNumber");

        [System.Text.Json.Serialization.JsonPropertyName("___name")]
        public string Name => GetValue("name", "GuideName");

        [System.Text.Json.Serialization.JsonPropertyName("___callsign")]
        public string CallSign => GetValue("callsign", "station", "tmsid");

        [System.Text.Json.Serialization.JsonPropertyName("___station")]
        public string StationId => GetValue("station");

        private string? _imageUrlOverride;

        [System.Text.Json.Serialization.JsonPropertyName("___image")]
        public string ImageUrl 
        {
            get 
            {
                if (!string.IsNullOrEmpty(_imageUrlOverride)) return _imageUrlOverride;
                return GetValue("image", "logo", "art", "thumbnail"); 
            }
            set => _imageUrlOverride = value;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public List<Airing>? CurrentAirings { get; set; }

        private string GetValue(params string[] searchKeys)
        {
            if (ExtraData != null)
            {
                foreach (var key in searchKeys)
                {
                    var match = ExtraData.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (match.Key != null && match.Value.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        return match.Value.ToString().Trim();
                    }
                }
            }
            return string.Empty;
        }

        public bool HasIdentifier(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            query = query.Trim();

            if (query.Equals(Number, StringComparison.OrdinalIgnoreCase)) return true;
            if (query.Equals(Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (query.Equals(Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (query.Equals(CallSign, StringComparison.OrdinalIgnoreCase)) return true;

            if (ExtraData != null)
            {
                foreach (var kvp in ExtraData)
                {
                    if (kvp.Value.ValueKind == System.Text.Json.JsonValueKind.String || 
                        kvp.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        if (query.Equals(kvp.Value.ToString().Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
    
    public class GuideData
    {
        [JsonPropertyName("Channel")] public JsonElement? ChannelRaw { get; set; }

        [JsonIgnore]
        public string? ChannelNumber 
        {
            get 
            {
                if (!ChannelRaw.HasValue || ChannelRaw.Value.ValueKind == JsonValueKind.Null) return null;
                var el = ChannelRaw.Value;
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var e = el.EnumerateArray();
                    if (e.MoveNext()) el = e.Current;
                    else return null;
                }
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("GuideNumber", out var gn)) return gn.ToString();
                    if (el.TryGetProperty("Number", out var num)) return num.ToString();
                }
                return el.ValueKind == JsonValueKind.String ? el.GetString().Trim() : el.ToString().Trim();
            }
        }
		
		[JsonIgnore]
public string? ChannelImageUrl 
{
    get 
    {
        if (!ChannelRaw.HasValue) return null;
        var el = ChannelRaw.Value;
        
        if (el.ValueKind == JsonValueKind.Array)
        {
            var e = el.EnumerateArray();
            if (e.MoveNext()) el = e.Current;
            else return null;
        }
        
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("Image", out var img) && img.ValueKind == JsonValueKind.String) return img.GetString();
            if (el.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.String) return logo.GetString();
        }
        return null;
    }
}

        [JsonPropertyName("Airings")] public List<Airing>? Airings { get; set; }
    }
    
    public class ChannelCollection
    {
        public string slug { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public List<string> items { get; set; } = new List<string>(); 
    }

    public class Airing
    {
        [JsonIgnore] public string? ChannelNumber { get; set; }

        [JsonPropertyName("Title")] public string? Title { get; set; }
        [JsonPropertyName("EpisodeTitle")] public string? EpisodeTitle { get; set; }
		[JsonPropertyName("Source")] public string? Source { get; set; }

        [JsonIgnore]
        public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : 
                                      (!string.IsNullOrWhiteSpace(EpisodeTitle) ? EpisodeTitle : "No Title");

        [JsonPropertyName("Summary")] public JsonElement? SummaryRaw { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
		
		[JsonIgnore]
        public double LeftOffset { get; set; } = 0;

        [JsonIgnore]
        public System.Windows.Thickness DynamicMargin => new System.Windows.Thickness(LeftOffset, 0, 0, 0);

        // NEW: If the block is pushed left (negative), push the text right (positive) by the exact same amount!
        [JsonIgnore]
        public System.Windows.Thickness InnerContentMargin => new System.Windows.Thickness(LeftOffset < 0 ? Math.Abs(LeftOffset) : 0, 0, 0, 0);

        [JsonIgnore]
        public string DisplaySummary 
        {
            get
            {
                if (SummaryRaw.HasValue && SummaryRaw.Value.ValueKind == JsonValueKind.String)
                    return SummaryRaw.Value.GetString() ?? "No description available.";
                
                if (ExtensionData != null && ExtensionData.TryGetValue("FullSummary", out var fs) && fs.ValueKind == JsonValueKind.String)
                    return fs.GetString() ?? "No description available.";
                    
                return "No description available.";
            }
        }

        [JsonPropertyName("Time")] public JsonElement? TimeRaw { get; set; }

        [JsonIgnore]
        public DateTime StartTime
        {
            get
            {
                if (TimeRaw.HasValue && TimeRaw.Value.ValueKind == JsonValueKind.Number)
                {
                    long unixTime = TimeRaw.Value.GetInt64();
                    return DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
                }
                return DateTime.MinValue;
            }
        }

        [JsonPropertyName("Duration")] public JsonElement? DurationRaw { get; set; } 

        [JsonIgnore]
        public double? Duration 
        {
            get 
            {
                if (!DurationRaw.HasValue) return null;
                if (DurationRaw.Value.ValueKind == JsonValueKind.Number) return DurationRaw.Value.GetDouble();
                if (DurationRaw.Value.ValueKind == JsonValueKind.String && double.TryParse(DurationRaw.Value.GetString(), out double d)) return d;
                return null;
            }
        }
		
		[JsonPropertyName("Categories")] public List<string>? Categories { get; set; }
        [JsonPropertyName("Genres")] public List<string>? Genres { get; set; }
		[JsonPropertyName("SeasonNumber")] public int? SeasonNumber { get; set; }
        [JsonPropertyName("EpisodeNumber")] public int? EpisodeNumber { get; set; }
        [JsonPropertyName("OriginalDate")] public string? OriginalDate { get; set; }

        [JsonIgnore]
        public string DisplayMetaData
        {
            get
            {
                var parts = new List<string>();
                
                // 1. Add Season/Episode if they exist
                if (SeasonNumber.HasValue && SeasonNumber > 0 && EpisodeNumber.HasValue && EpisodeNumber > 0)
                {
                    parts.Add($"S{SeasonNumber} E{EpisodeNumber}");
                }
                
                // 2. Add the release year
                if (!string.IsNullOrWhiteSpace(OriginalDate) && OriginalDate.Length >= 4)
                {
                    parts.Add(OriginalDate.Substring(0, 4)); 
                }
                
                // 3. Add the primary genre
                var firstGenre = (Genres?.FirstOrDefault() ?? Categories?.FirstOrDefault());
                if (!string.IsNullOrWhiteSpace(firstGenre))
                {
                    parts.Add(firstGenre);
                }
                
                return string.Join("  •  ", parts);
            }
        }

        [JsonIgnore]
        public string CategoryColor
        {
            get
            {
                // 1. Gather all possible tags from the DVR
                var tags = new List<string>();
                if (Categories != null) tags.AddRange(Categories);
                if (Genres != null) tags.AddRange(Genres);
                
                // 2. Smash them into one giant lowercase string for easy searching
                var combined = string.Join(" ", tags).ToLower();
                
                // 3. Search for keywords
                if (combined.Contains("sports") || combined.Contains("event") || combined.Contains("athletics")) return "#E87C00"; // Orange
                if (combined.Contains("news") || combined.Contains("local")) return "#107C10"; // Green
                if (combined.Contains("movie") || combined.Contains("film") || combined.Contains("cinema")) return "#9300BA"; // Purple
                if (combined.Contains("kids") || combined.Contains("children") || combined.Contains("animation")) return "#00A4EF"; // Light Blue
                
                // 4. THE TEST COLOR: We return a visible grey instead of Transparent. 
                // If you see grey bars, the XAML works perfectly!
                return "Transparent"; 
            }
        }

        [JsonIgnore]
        public double BlockWidth => ((Duration ?? 1800) / 60.0) * 8.0; 

        [JsonIgnore]
        public bool IsAiringNow
        {
            get
            {
                if (StartTime == DateTime.MinValue || Duration == null) return false;
                DateTime endTime = StartTime.AddSeconds(Duration.Value);
                return DateTime.Now >= StartTime && DateTime.Now < endTime;
            }
        }
        
        [JsonPropertyName("Image")] 
        public JsonElement? ImageRaw { get; set; }

        [JsonIgnore]
        public string? ImageUrl
        {
            get
            {
                if (ImageRaw.HasValue && ImageRaw.Value.ValueKind == JsonValueKind.String)
                    return ImageRaw.Value.GetString();
                return null;
            }
        }

        [JsonIgnore] 
        public string? ChannelLogoUrl { get; set; }
    }
	
	// --- NEW: The Data Model for Recorded Movies ---
    public class Movie
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; } = "Unknown Title";
		
		[System.Text.Json.Serialization.JsonPropertyName("commercials")]
        public List<double>? Commercials { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("summary")]
        public string Summary { get; set; } = "No summary available.";

        [System.Text.Json.Serialization.JsonPropertyName("duration")]
        public double? Duration { get; set; } 

        [System.Text.Json.Serialization.JsonPropertyName("release_year")]
        public int? ReleaseYear { get; set; }
		
		[System.Text.Json.Serialization.JsonPropertyName("genres")] 
        public List<string>? Genres { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("watched")] 
        public bool Watched { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("created_at")] 
        public long CreatedAt { get; set; }

        // We use image_url based on your JSON sample!
        [System.Text.Json.Serialization.JsonPropertyName("image_url")]
        public string RawImage { get; set; } = "";

        // Helper property to hold the formatted URL for the UI
        public string PosterUrl { get; set; } = "";

        // Helper property to convert seconds into a clean "1h 45m" format
        public string DisplayDuration
        {
            get
            {
                if (!Duration.HasValue || Duration <= 0) return "";
                TimeSpan time = TimeSpan.FromSeconds(Duration.Value);
                if (time.Hours > 0) return $"{time.Hours}h {time.Minutes}m";
                return $"{time.Minutes}m";
            }
        }
    }

    public class Station
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("logo")]
        public string? Logo { get; set; }
    }
	
	public class TvShow
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("image_url")] public string ImageUrl { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
		
		[System.Text.Json.Serialization.JsonPropertyName("genres")] 
        public List<string>? Genres { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("release_year")] 
        public int? ReleaseYear { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("created_at")] 
        public long CreatedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("number_unwatched")] 
        public int NumberUnwatched { get; set; }

        // Helper to check if the whole show is watched
        [System.Text.Json.Serialization.JsonIgnore] 
        public bool IsWatched => NumberUnwatched == 0;
    }

    public class Episode
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("show_id")] public string ShowId { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string Title { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("episode_title")] public string EpisodeTitle { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("image_url")] public string ImageUrl { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("commercials")] public List<double>? Commercials { get; set; }
    }
}