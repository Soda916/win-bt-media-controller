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
        private readonly BluetoothDeviceManager _btManager = new();
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _activeSession;
        private DispatcherTimer? _pollTimer;

        public event EventHandler<MediaInfoEventArgs>? MediaInfoUpdated;
        public event EventHandler<bool>? PlaybackStateUpdated;
        public event EventHandler<string>? StatusUpdated;

        public async Task InitializeAsync()
        {
            // 1. 綁定藍芽裝置管理器事件
            _btManager.OnStatusMessage += (s, msg) => StatusUpdated?.Invoke(this, msg);
            _btManager.OnMediaUpdated += async (s, e) =>
            {
                var artwork = await _artworkService.FetchArtworkAsync(e.Title, e.Artist);
                MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                {
                    Title = e.Title,
                    Artist = e.Artist,
                    Album = e.Album,
                    Thumbnail = artwork,
                    IsPlaying = e.IsPlaying,
                    SourceApp = _btManager.ConnectedDeviceName
                });
                PlaybackStateUpdated?.Invoke(this, e.IsPlaying);
            };

            // 啟動已配對裝置掃描與連線 (在背景執行不阻塞 UI)
            _ = _btManager.ScanAndConnectPairedDevicesAsync();

            // 2. 備援初始化 GSMTC SessionManager
            try
            {
                Logger.Log("[Init] 正在請求 GSMTC SessionManager...");
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                if (_manager != null)
                {
                    Logger.Log("[Init] GSMTC SessionManager 請求成功！");
                    _manager.CurrentSessionChanged += (s, e) => RefreshActiveSession();
                    _manager.SessionsChanged += (s, e) => RefreshActiveSession();

                    _pollTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(2000)
                    };
                    _pollTimer.Tick += (s, e) => RefreshActiveSession();
                    _pollTimer.Start();

                    RefreshActiveSession();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Init] GSMTC 異常: {ex.Message}");
            }
        }

        public async void RefreshAllAsync()
        {
            Logger.Log("[RefreshAll] 手動觸發全盤掃描與連線...");
            await _btManager.ScanAndConnectPairedDevicesAsync();
            RefreshActiveSession();
        }

        public async void RefreshActiveSession()
        {
            // 如果 AMS 已經連線，以 AMS 為準
            if (_btManager.IsAmsConnected) return;
            if (_manager == null) return;

            try
            {
                var sessions = _manager.GetSessions();
                GlobalSystemMediaTransportControlsSession? targetSession = null;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    var info = s.GetPlaybackInfo();
                    if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        targetSession = s;
                        break;
                    }
                }

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
                        _activeSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                        _activeSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                    }
                }

                if (_activeSession != null)
                {
                    await UpdateGsmtcInfoAsync(_activeSession);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[RefreshActiveSession] 異常: {ex.Message}");
            }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            if (_btManager.IsAmsConnected) return;
            await UpdateGsmtcInfoAsync(sender);
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            if (_btManager.IsAmsConnected) return;
            var playbackInfo = sender.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlaybackStateUpdated?.Invoke(this, isPlaying);
            }
        }

        private async Task UpdateGsmtcInfoAsync(GlobalSystemMediaTransportControlsSession session)
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
            catch { }
        }

        public async Task NextAsync()
        {
            Logger.Log("[Action] 發送 Next 指令");

            // 1. AMS 優先
            if (_btManager.IsAmsConnected)
            {
                if (await _btManager.NextTrackAsync()) return;
            }

            // 2. GSMTC 次之
            if (_activeSession != null)
            {
                if (await _activeSession.TrySkipNextAsync()) return;
            }

            // 3. Win32 備援
            SendMediaKey(VK_MEDIA_NEXT_TRACK);
        }

        public async Task PreviousAsync()
        {
            Logger.Log("[Action] 發送 Previous 指令");

            if (_btManager.IsAmsConnected)
            {
                if (await _btManager.PreviousTrackAsync()) return;
            }

            if (_activeSession != null)
            {
                if (await _activeSession.TrySkipPreviousAsync()) return;
            }

            SendMediaKey(VK_MEDIA_PREV_TRACK);
        }

        public async Task TogglePlayPauseAsync()
        {
            Logger.Log("[Action] 發送 TogglePlayPause 指令");

            if (_btManager.IsAmsConnected)
            {
                if (await _btManager.TogglePlayPauseAsync()) return;
            }

            if (_activeSession != null)
            {
                if (await _activeSession.TryTogglePlayPauseAsync()) return;
            }

            SendMediaKey(VK_MEDIA_PLAY_PAUSE);
        }

        private static void SendMediaKey(byte key)
        {
            keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, 0);
            keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
        }
    }
}
