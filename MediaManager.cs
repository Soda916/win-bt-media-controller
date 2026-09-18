using System;
using System.Linq;
using System.Runtime.InteropServices;
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
        public string SourceApp { get; set; } = "";
    }

    public class MediaManager
    {
        // Win32 虛擬多媒體按鍵 (備援發送)
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_STOP = 0xB2;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const int KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private readonly ArtworkService _artworkService = new();
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _activeSession;

        public event EventHandler<MediaInfoEventArgs>? MediaInfoUpdated;
        public event EventHandler<bool>? PlaybackStateUpdated;
        public event EventHandler<string>? StatusUpdated;

        public async Task InitializeAsync()
        {
            try
            {
                StatusUpdated?.Invoke(this, "正在初始化 Windows 媒體會話監聽...");
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += (s, e) => RefreshActiveSession();
                    _manager.SessionsChanged += (s, e) => RefreshActiveSession();
                    RefreshActiveSession();
                }
                else
                {
                    StatusUpdated?.Invoke(this, "無法存取 Windows 媒體控制服務");
                }
            }
            catch (Exception ex)
            {
                StatusUpdated?.Invoke(this, $"初始化失敗: {ex.Message}");
            }
        }

        public void RefreshActiveSession()
        {
            if (_manager == null) return;

            try
            {
                var sessions = _manager.GetSessions();
                StatusUpdated?.Invoke(this, $"偵測到 {sessions.Count} 個媒體會話");

                // 優先挑選正在播放的會話，或最後一個活動會話
                var session = sessions.FirstOrDefault(s =>
                {
                    var info = s.GetPlaybackInfo();
                    return info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }) ?? _manager.GetCurrentSession() ?? sessions.LastOrDefault();

                if (_activeSession != null)
                {
                    _activeSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                    _activeSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                }

                _activeSession = session;

                if (_activeSession != null)
                {
                    _activeSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                    _activeSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                    _ = UpdateMediaInfoAsync(_activeSession);
                }
                else
                {
                    MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                    {
                        Title = "等待手機/媒體播放中...",
                        Artist = "請在手機點擊播放",
                        Album = "",
                        Thumbnail = null,
                        IsPlaying = false,
                        SourceApp = ""
                    });
                }
            }
            catch (Exception ex)
            {
                StatusUpdated?.Invoke(this, $"會話刷新失敗: {ex.Message}");
            }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await UpdateMediaInfoAsync(sender);
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            var playbackInfo = sender.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlaybackStateUpdated?.Invoke(this, isPlaying);
            }
        }

        private async Task UpdateMediaInfoAsync(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playbackInfo = session.GetPlaybackInfo();
                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                string title = string.IsNullOrWhiteSpace(props?.Title) ? "未知曲目" : props.Title;
                string artist = string.IsNullOrWhiteSpace(props?.Artist) ? "未知歌手" : props.Artist;
                string album = props?.AlbumTitle ?? "";

                // 背景搜尋 600x600 高畫質封面
                var artwork = await _artworkService.FetchArtworkAsync(title, artist);

                MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Thumbnail = artwork,
                    IsPlaying = isPlaying,
                    SourceApp = session.SourceAppId ?? ""
                });

                PlaybackStateUpdated?.Invoke(this, isPlaying);
                StatusUpdated?.Invoke(this, $"正在監聽: {session.SourceAppId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaManager] UpdateMediaInfo Error: {ex.Message}");
            }
        }

        public async Task NextAsync()
        {
            bool handled = false;
            if (_activeSession != null)
            {
                handled = await _activeSession.TrySkipNextAsync();
            }

            // 如果 Session 控制未被處理，發送系統硬體媒體鍵 (保證能切歌)
            if (!handled)
            {
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
            }
        }

        public async Task PreviousAsync()
        {
            bool handled = false;
            if (_activeSession != null)
            {
                handled = await _activeSession.TrySkipPreviousAsync();
            }

            if (!handled)
            {
                SendMediaKey(VK_MEDIA_PREV_TRACK);
            }
        }

        public async Task TogglePlayPauseAsync()
        {
            bool handled = false;
            if (_activeSession != null)
            {
                handled = await _activeSession.TryTogglePlayPauseAsync();
            }

            if (!handled)
            {
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
            }
        }

        private static void SendMediaKey(byte key)
        {
            keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, 0);
            keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
        }
    }
}
