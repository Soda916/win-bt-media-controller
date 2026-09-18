using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace MediaController
{
    public class ArtworkService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private readonly ConcurrentDictionary<string, BitmapImage> _cache = new();

        public async Task<BitmapImage?> FetchArtworkAsync(string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title) || title == "未知曲目")
                return null;

            string queryKey = $"{artist} - {title}".Trim(' ', '-');
            if (_cache.TryGetValue(queryKey, out var cachedImage))
            {
                return cachedImage;
            }

            try
            {
                string searchTerm = Uri.EscapeDataString($"{artist} {title}".Trim());
                string url = $"https://itunes.apple.com/search?term={searchTerm}&entity=song&limit=1";

                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("resultCount", out var count) && count.GetInt32() > 0)
                {
                    var results = root.GetProperty("results");
                    var firstResult = results[0];

                    if (firstResult.TryGetProperty("artworkUrl100", out var artworkUrlProp))
                    {
                        string? artworkUrl = artworkUrlProp.GetString();
                        if (!string.IsNullOrEmpty(artworkUrl))
                        {
                            // 替換成 600x600 高畫質圖
                            string hdUrl = artworkUrl.Replace("100x100bb", "600x600bb");
                            var imageBytes = await _httpClient.GetByteArrayAsync(hdUrl);

                            var bitmap = new BitmapImage();
                            using var memStream = new MemoryStream(imageBytes);
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = memStream;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            _cache[queryKey] = bitmap;
                            return bitmap;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ArtworkService] Fetch error: {ex.Message}");
            }

            return null;
        }
    }
}
