using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace MediaController
{
    public class MediaInfoEventArgs : EventArgs
    {
        public string Title { get; set; } = "未知曲目";
        public string Artist { get; set; } = "未知歌手";
        public string Album { get; set; } = "";
        public BitmapImage? Thumbnail { get; set; }
        public bool IsPlaying { get; set; }
    }

    public class MediaManager
    {
        private readonly AmsBleManager _amsManager = new();
        private readonly ArtworkService _artworkService = new();
        private GlobalSystemMediaTransportControlsSessionManager? _gsmtcManager;
        private GlobalSystemMediaTransportControlsSession? _currentGsmtcSession;

        public event EventHandler<MediaInfoEventArgs>? MediaInfoUpdated;
        public event EventHandler<bool>? PlaybackStateUpdated;
        public event EventHandler<string>? StatusUpdated;

        public async Task InitializeAsync()
        {
            // 1. 綁定 AMS 藍芽事件
            _amsManager.MediaInfoChanged += AmsManager_MediaInfoChanged;
            _amsManager.StatusChanged += (s, msg) => StatusUpdated?.Invoke(this, msg);

            // 啟動 AMS 藍芽掃描與連線 (直連 iPhone，完全不碰音訊)
            await _amsManager.StartAsync();

            // 2. 初始化 GSMTC 作為次要備援 (支援本機或標準 AVRCP)
            try
            {
                _gsmtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_gsmtcManager != null)
                {
                    _gsmtcManager.CurrentSessionChanged += (s, e) => UpdateGsmtcSession();
                    UpdateGsmtcSession();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GSMTC] Fallback init error: {ex.Message}");
            }
        }

        private async void AmsManager_MediaInfoChanged(object? sender, AmsMediaEventArgs e)
        {
            // 當 AMS 收到 iPhone 即時歌名與歌手，立即在背景搜尋高畫質專輯封面
            var artwork = await _artworkService.FetchArtworkAsync(e.Title, e.Artist);

            MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
            {
                Title = e.Title,
                Artist = e.Artist,
                Album = e.Album,
                Thumbnail = artwork,
                IsPlaying = e.IsPlaying
            });

            PlaybackStateUpdated?.Invoke(this, e.IsPlaying);
        }

        private void UpdateGsmtcSession()
        {
            if (_amsManager.IsConnected || _gsmtcManager == null) return;

            _currentGsmtcSession = _gsmtcManager.GetCurrentSession();
            if (_currentGsmtcSession != null)
            {
                _ = RefreshGsmtcInfoAsync();
            }
        }

        private async Task RefreshGsmtcInfoAsync()
        {
            if (_amsManager.IsConnected || _currentGsmtcSession == null) return;

            try
            {
                var mediaProps = await _currentGsmtcSession.TryGetMediaPropertiesAsync();
                var playbackInfo = _currentGsmtcSession.GetPlaybackInfo();
                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                string title = string.IsNullOrWhiteSpace(mediaProps?.Title) ? "未知曲目" : mediaProps.Title;
                string artist = string.IsNullOrWhiteSpace(mediaProps?.Artist) ? "未知歌手" : mediaProps.Artist;

                var artwork = await _artworkService.FetchArtworkAsync(title, artist);

                MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                {
                    Title = title,
                    Artist = artist,
                    Album = mediaProps?.AlbumTitle ?? "",
                    Thumbnail = artwork,
                    IsPlaying = isPlaying
                });

                PlaybackStateUpdated?.Invoke(this, isPlaying);
            }
            catch { }
        }

        public async Task<bool> NextAsync()
        {
            if (_amsManager.IsConnected)
            {
                await _amsManager.NextTrackAsync();
                return true;
            }
            if (_currentGsmtcSession != null)
            {
                return await _currentGsmtcSession.TrySkipNextAsync();
            }
            return false;
        }

        public async Task<bool> PreviousAsync()
        {
            if (_amsManager.IsConnected)
            {
                await _amsManager.PreviousTrackAsync();
                return true;
            }
            if (_currentGsmtcSession != null)
            {
                return await _currentGsmtcSession.TrySkipPreviousAsync();
            }
            return false;
        }

        public async Task<bool> TogglePlayPauseAsync()
        {
            if (_amsManager.IsConnected)
            {
                await _amsManager.TogglePlayPauseAsync();
                return true;
            }
            if (_currentGsmtcSession != null)
            {
                return await _currentGsmtcSession.TryTogglePlayPauseAsync();
            }
            return false;
        }
    }
}
