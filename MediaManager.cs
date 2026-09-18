using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const int KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private readonly ArtworkService _artworkService = new();
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _activeSession;
        private DispatcherTimer? _pollTimer;

        public event EventHandler<MediaInfoEventArgs>? MediaInfoUpdated;
        public event EventHandler<bool>? PlaybackStateUpdated;
        public event EventHandler<string>? StatusUpdated;

        public async Task InitializeAsync()
        {
            Logger.Log("[Init] 正在請求 GSMTC SessionManager...");
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                if (_manager != null)
                {
                    Logger.Log("[Init] GSMTC SessionManager 請求成功！");
                    _manager.CurrentSessionChanged += (s, e) =>
                    {
                        Logger.Log("[Event] CurrentSessionChanged 觸發");
                        RefreshActiveSession();
                    };
                    _manager.SessionsChanged += (s, e) =>
                    {
                        Logger.Log("[Event] SessionsChanged 觸發");
                        RefreshActiveSession();
                    };

                    // 建立 1.5 秒定時巡檢器，防止部分藍芽驅動漏發事件
                    _pollTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1500)
                    };
                    _pollTimer.Tick += (s, e) => RefreshActiveSession();
                    _pollTimer.Start();

                    RefreshActiveSession();
                }
                else
                {
                    Logger.Log("[Error] GSMTC SessionManager 為 null，可能是系統不支援或權限受阻");
                    StatusUpdated?.Invoke(this, "無法存取 Windows 媒體控制服務");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Exception] InitializeAsync 異常: {ex}");
                StatusUpdated?.Invoke(this, $"初始化失敗: {ex.Message}");
            }
        }

        public async void RefreshActiveSession()
        {
            if (_manager == null) return;

            try
            {
                var sessions = _manager.GetSessions();
                Logger.Log($"[Scan] 找到 {sessions.Count} 個媒體會話");

                GlobalSystemMediaTransportControlsSession? targetSession = null;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    var info = s.GetPlaybackInfo();
                    string appId = s.SourceAppUserModelId ?? "UnknownApp";
                    string status = info?.PlaybackStatus.ToString() ?? "Unknown";

                    Logger.Log($"  - Session[{i}]: App={appId}, Status={status}");

                    if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        targetSession = s;
                    }
                }

                // 若無 Playing 會話，依序退回到 CurrentSession 或最後一個會話
                targetSession ??= _manager.GetCurrentSession() ?? sessions.LastOrDefault();

                if (_activeSession != targetSession)
                {
                    if (_activeSession != null)
                    {
                        _activeSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                        _activeSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                    }

                    _activeSession = targetSession;

                    if (_activeSession != null)
                    {
                        Logger.Log($"[Active] 切換目標會話至: {_activeSession.SourceAppUserModelId}");
                        _activeSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                        _activeSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                    }
                }

                if (_activeSession != null)
                {
                    await UpdateMediaInfoAsync(_activeSession);
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
                Logger.Log($"[Exception] RefreshActiveSession 異常: {ex}");
                StatusUpdated?.Invoke(this, $"會話刷新失敗: {ex.Message}");
            }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            Logger.Log($"[Event] MediaPropertiesChanged: {sender.SourceAppUserModelId}");
            await UpdateMediaInfoAsync(sender);
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            var playbackInfo = sender.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                Logger.Log($"[Event] PlaybackInfoChanged: {sender.SourceAppUserModelId} -> IsPlaying={isPlaying}");
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
                string appId = session.SourceAppUserModelId ?? "";

                Logger.Log($"[Track] 收到曲目: '{title}' - '{artist}' (Album: '{album}') [App: {appId}]");

                // 背景搜尋 600x600 高畫質封面
                var artwork = await _artworkService.FetchArtworkAsync(title, artist);

                MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Thumbnail = artwork,
                    IsPlaying = isPlaying,
                    SourceApp = appId
                });

                PlaybackStateUpdated?.Invoke(this, isPlaying);
                StatusUpdated?.Invoke(this, $"已鎖定: {appId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Exception] UpdateMediaInfoAsync 異常: {ex}");
            }
        }

        public async Task NextAsync()
        {
            Logger.Log("[Action] 發送 Next 指令");
            bool handled = false;
            if (_activeSession != null)
            {
                try
                {
                    handled = await _activeSession.TrySkipNextAsync();
                    Logger.Log($"[Action] Session.TrySkipNextAsync 結果: {handled}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Action] TrySkipNextAsync 異常: {ex.Message}");
                }
            }

            if (!handled)
            {
                Logger.Log("[Action] 透過 Win32 keybd_event 發送 VK_MEDIA_NEXT_TRACK 備援按鍵");
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
            }
        }

        public async Task PreviousAsync()
        {
            Logger.Log("[Action] 發送 Previous 指令");
            bool handled = false;
            if (_activeSession != null)
            {
                try
                {
                    handled = await _activeSession.TrySkipPreviousAsync();
                    Logger.Log($"[Action] Session.TrySkipPreviousAsync 結果: {handled}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Action] TrySkipPreviousAsync 異常: {ex.Message}");
                }
            }

            if (!handled)
            {
                Logger.Log("[Action] 透過 Win32 keybd_event 發送 VK_MEDIA_PREV_TRACK 備援按鍵");
                SendMediaKey(VK_MEDIA_PREV_TRACK);
            }
        }

        public async Task TogglePlayPauseAsync()
        {
            Logger.Log("[Action] 發送 TogglePlayPause 指令");
            bool handled = false;
            if (_activeSession != null)
            {
                try
                {
                    handled = await _activeSession.TryTogglePlayPauseAsync();
                    Logger.Log($"[Action] Session.TryTogglePlayPauseAsync 結果: {handled}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Action] TryTogglePlayPauseAsync 異常: {ex.Message}");
                }
            }

            if (!handled)
            {
                Logger.Log("[Action] 透過 Win32 keybd_event 發送 VK_MEDIA_PLAY_PAUSE 備援按鍵");
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
