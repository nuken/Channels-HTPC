#pragma warning disable CS8602
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Linq;
using Zeroconf;

namespace FeralCode
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

        // --- NEW: 5-Minute Memory Cache Fields ---
        private static List<TvShow>? _cachedShows;
        private static DateTime _lastShowsFetch = DateTime.MinValue;

        private static List<Episode>? _cachedEpisodes;
        private static DateTime _lastEpisodesFetch = DateTime.MinValue;

        private static List<Channel>? _cachedChannels;
        private static DateTime _lastChannelsFetch = DateTime.MinValue;

        private static List<ChannelCollection>? _cachedCollections;
        private static DateTime _lastCollectionsFetch = DateTime.MinValue;

        private static List<GuideData>? _cachedGuide;
        private static DateTime _lastGuideFetch = DateTime.MinValue;
        private static int _lastGuideDuration = 0;

        private static List<Movie>? _cachedMovies;
        private static DateTime _lastMoviesFetch = DateTime.MinValue;

        private static List<Station>? _cachedStations;
        private static DateTime _lastStationsFetch = DateTime.MinValue;

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
            if (_cachedShows != null && (DateTime.Now - _lastShowsFetch).TotalMinutes < 5) return _cachedShows;

            try {
                var json = await _http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/v1/shows");
                _cachedShows = System.Text.Json.JsonSerializer.Deserialize<List<TvShow>>(json) ?? new List<TvShow>();
                _lastShowsFetch = DateTime.Now;
                return _cachedShows;
            } catch { return new List<TvShow>(); }
        }

        public async Task<List<Episode>> GetEpisodesAsync(string baseUrl)
        {
            if (_cachedEpisodes != null && (DateTime.Now - _lastEpisodesFetch).TotalMinutes < 5) return _cachedEpisodes;

            try {
                var json = await _http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/v1/episodes");
                var episodes = System.Text.Json.JsonSerializer.Deserialize<List<Episode>>(json) ?? new List<Episode>();
                
                // --- NEW: Filter out stream links from the library ---
                episodes.RemoveAll(e => !string.IsNullOrWhiteSpace(e.Path) && 
                                       (e.Path.EndsWith(".strmlnk", StringComparison.OrdinalIgnoreCase) || 
                                        e.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)));

                _cachedEpisodes = episodes;
                _lastEpisodesFetch = DateTime.Now;
                return _cachedEpisodes;
            } catch { return new List<Episode>(); }
        }

        public async Task<List<Channel>> GetChannelsAsync(string baseUrl)
        {
            if (_cachedChannels != null && (DateTime.Now - _lastChannelsFetch).TotalMinutes < 5) return _cachedChannels;

            var url = $"{baseUrl}/devices/ANY/channels";
            var response = await _http.GetStringAsync(url);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            _cachedChannels = JsonSerializer.Deserialize<List<Channel>>(response, options) ?? new List<Channel>();
            _lastChannelsFetch = DateTime.Now;
            return _cachedChannels;
        }
        
        public async Task<List<ChannelCollection>> GetChannelCollectionsAsync(string baseUrl)
        {
            if (_cachedCollections != null && (DateTime.Now - _lastCollectionsFetch).TotalMinutes < 5) return _cachedCollections;

            try
            {
                string url = $"{baseUrl}/dvr/collections/channels";
                var response = await _http.GetStringAsync(url);
                var collections = JsonSerializer.Deserialize<List<ChannelCollection>>(response, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                _cachedCollections = collections ?? new List<ChannelCollection>();
                _lastCollectionsFetch = DateTime.Now;
                return _cachedCollections;
            }
            catch
            {
                return new List<ChannelCollection>();
            }
        }

        public async Task<List<GuideData>> GetGuideAsync(string baseUrl, int durationHours = 4)
        {
            // Only use cache if it's less than 5 minutes old AND the requested duration hasn't changed
            if (_cachedGuide != null && _lastGuideDuration == durationHours && (DateTime.Now - _lastGuideFetch).TotalMinutes < 5)
            {
                return _cachedGuide;
            }

            long unixTime = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds();
            long durationSeconds = durationHours * 3600; // Convert hours to seconds
            
            var url = $"{baseUrl}/devices/ANY/guide?time={unixTime}&duration={durationSeconds}";
            var response = await _http.GetStringAsync(url);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            
            _cachedGuide = JsonSerializer.Deserialize<List<GuideData>>(response, options) ?? new List<GuideData>();
            _lastGuideDuration = durationHours;
            _lastGuideFetch = DateTime.Now;
            
            return _cachedGuide;
        }
		
        public async Task<List<Movie>> GetMoviesAsync(string baseUrl)
        {
            if (_cachedMovies != null && (DateTime.Now - _lastMoviesFetch).TotalMinutes < 5) return _cachedMovies;

            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                string url = $"{baseUrl.TrimEnd('/')}/api/v1/movies";
                var response = await _http.GetStringAsync(url);
                
                var movies = System.Text.Json.JsonSerializer.Deserialize<List<Movie>>(response, options) ?? new List<Movie>();

                // --- NEW: Filter out stream links from the library ---
                movies.RemoveAll(m => !string.IsNullOrWhiteSpace(m.Path) && 
                                     (m.Path.EndsWith(".strmlnk", StringComparison.OrdinalIgnoreCase) || 
                                      m.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)));

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
                _cachedMovies = movies.OrderBy(m => m.Title).ToList();
                _lastMoviesFetch = DateTime.Now;
                return _cachedMovies;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch movies: {ex.Message}");
                return new List<Movie>();
            }
        }

        public async Task<List<Station>> GetStationsAsync(string baseUrl)
        {
            if (_cachedStations != null && (DateTime.Now - _lastStationsFetch).TotalMinutes < 5) return _cachedStations;

            var stationsList = new List<Station>();
            try
            {
                var url = $"{baseUrl}/dvr/guide/stations";
                var response = await _http.GetStringAsync(url);
                
                using var document = JsonDocument.Parse(response);
                
                // The JSON root is a dictionary of providers (e.g., "USA-OTA", "X-M3U")
                foreach (var provider in document.RootElement.EnumerateObject())
                {
                    // Each provider contains a dictionary of Station IDs
                    foreach (var stationProp in provider.Value.EnumerateObject())
                    {
                        string stationId = stationProp.Name;
                        string logoUrl = "";
                        
                        // 1. Standard Broadcast/Cable (Gracenote data)
                        if (stationProp.Value.TryGetProperty("preferredImage", out var prefImg) && 
                            prefImg.TryGetProperty("uri", out var uriProp))
                        {
                            logoUrl = uriProp.GetString() ?? "";
                        }
                        // 2. Custom M3U / Pluto TV data
                        else if (stationProp.Value.TryGetProperty("Icon", out var iconProp) && 
                                 iconProp.TryGetProperty("Src", out var srcProp))
                        {
                            logoUrl = srcProp.GetString() ?? "";
                        }

                        if (!string.IsNullOrWhiteSpace(stationId) && !string.IsNullOrWhiteSpace(logoUrl))
                        {
                            stationsList.Add(new Station { Id = stationId, Logo = logoUrl });
                        }
                    }
                }

                _cachedStations = stationsList;
                _lastStationsFetch = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load stations: {ex.Message}");
            }
            return stationsList;
        }
    }	
		
    public class Channel
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtraData { get; set; }
		
		private bool _favorite = false;
        private bool _favoriteChecked = false;

        // --- FIX: Ignore this during the strict JSON parse to prevent crashes! ---
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Favorite 
        {
            get 
            {
                if (!_favoriteChecked && ExtraData != null)
                {
                    var match = ExtraData.FirstOrDefault(x => string.Equals(x.Key, "Favorite", StringComparison.OrdinalIgnoreCase));
                    if (match.Key != null)
                    {
                        if (match.Value.ValueKind == System.Text.Json.JsonValueKind.True) _favorite = true;
                        else if (match.Value.ValueKind == System.Text.Json.JsonValueKind.False) _favorite = false;
                        else 
                        {
                            string val = match.Value.ToString().Trim();
                            _favorite = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
                        }
                    }
                    _favoriteChecked = true;
                }
                return _favorite;
            }
            set 
            {
                _favorite = value;
                _favoriteChecked = true;
            }
        }

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
		
        [System.Text.Json.Serialization.JsonPropertyName("CurrentShowTitle")]
        public string CurrentShowTitle => (CurrentAirings != null && CurrentAirings.Count > 0) ? CurrentAirings[0].DisplayTitle : "Unknown Programming";

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

    // --- FIX: Use IndexOf to allow for partial substring matches! ---
    if (Number.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
    if (Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
    if (Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
    if (CallSign.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;

    if (ExtraData != null)
    {
        foreach (var kvp in ExtraData)
        {
            if (kvp.Value.ValueKind == System.Text.Json.JsonValueKind.String || 
                kvp.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                string val = kvp.Value.ToString().Trim();
                if (val.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }
    }
    return false;
}

public bool IsExactMatch(string query)
{
    if (string.IsNullOrWhiteSpace(query)) return false;
    query = query.Trim();

    // For collections, we only want exact 1-to-1 matches, usually against the ID or Number
    if (string.Equals(Id, query, StringComparison.OrdinalIgnoreCase)) return true;
    if (string.Equals(Number, query, StringComparison.OrdinalIgnoreCase)) return true;
    if (string.Equals(CallSign, query, StringComparison.OrdinalIgnoreCase)) return true;

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
		
		[JsonIgnore]
        public bool IsFavorite 
        {
            get 
            {
                if (!ChannelRaw.HasValue) return false;
                var el = ChannelRaw.Value;
                
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var e = el.EnumerateArray();
                    if (e.MoveNext()) el = e.Current;
                    else return false;
                }
                
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("Favorite", out var fav))
                    {
                        if (fav.ValueKind == JsonValueKind.True) return true;
                        if (fav.ValueKind == JsonValueKind.False) return false;
                        string val = fav.ToString().Trim();
                        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
                    }
                }
                return false;
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
        public string DisplayTitle 
        {
            get
            {
                // If both exist, combine them (e.g., "Show Name - Episode Name")
                if (!string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(EpisodeTitle) && Title != EpisodeTitle)
                {
                    return $"{Title} - {EpisodeTitle}";
                }
                
                // Otherwise, just return whichever one is available
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(EpisodeTitle)) return EpisodeTitle;
                
                return "No Title";
            }
        }

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

        // --- NEW: Path for filtering out Stream Links ---
        [System.Text.Json.Serialization.JsonPropertyName("path")]
        public string Path { get; set; } = "";

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
		
        [System.Text.Json.Serialization.JsonPropertyName("content_rating")]
        public string? ContentRating { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("cast")]
        public List<string>? Cast { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("directors")]
        public List<string>? Directors { get; set; }

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

        // --- NEW: Path for filtering out Stream Links ---
        [System.Text.Json.Serialization.JsonPropertyName("path")] public string Path { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("show_id")] public string ShowId { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string Title { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("episode_title")] public string EpisodeTitle { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("image_url")] public string ImageUrl { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("commercials")] public List<double>? Commercials { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("content_rating")] public string? ContentRating { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("cast")] public List<string>? Cast { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("full_summary")] public string? FullSummary { get; set; }
    }
}