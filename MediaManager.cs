using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MediaController
{
    public class MediaInfoEventArgs : EventArgs
    {
        public string Title { get; set; } = "未知曲目";
        public string Artist { get; set; } = "未知歌手";
        public string Album { get; set; } = "";
        public BitmapImage? Thumbnail { get; set; }
        public bool IsPlaying { get; set; }
        public string StatusMessage { get; set; } = "";
    }

    public class MediaManager
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private readonly List<AudioPlaybackConnection> _activeConnections = new();
        private DeviceWatcher? _deviceWatcher;

        public event EventHandler<MediaInfoEventArgs>? MediaInfoUpdated;
        public event EventHandler<bool>? PlaybackStateUpdated;
        public event EventHandler<string>? StatusUpdated;

        public async Task InitializeAsync()
        {
            // 1. 啟動 A2DP Sink 音訊通道 (讓 Windows 主動偽裝成藍芽耳機/接收器)
            await InitializeBluetoothAudioSinkAsync();

            // 2. 初始化 GSMTC 媒體控制與事件監聽
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                    UpdateCurrentSession();
                }
            }
            catch (Exception ex)
            {
                StatusUpdated?.Invoke(this, $"媒體控制器初始化失敗: {ex.Message}");
            }
        }

        public async Task InitializeBluetoothAudioSinkAsync()
        {
            try
            {
                string selector = AudioPlaybackConnection.GetDeviceSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);

                int connectedCount = 0;
                foreach (var device in devices)
                {
                    bool ok = await ConnectAudioDeviceAsync(device.Id);
                    if (ok) connectedCount++;
                }

                if (connectedCount > 0)
                {
                    StatusUpdated?.Invoke(this, $"已開通 {connectedCount} 個藍芽音訊通道 (耳機模式)");
                }
                else
                {
                    StatusUpdated?.Invoke(this, "等待 iPhone 藍芽配對連線...");
                }

                // 註冊裝置監聽器，當手機後續連上時自動開通
                if (_deviceWatcher == null)
                {
                    _deviceWatcher = DeviceInformation.CreateWatcher(selector);
                    _deviceWatcher.Added += async (s, e) =>
                    {
                        await ConnectAudioDeviceAsync(e.Id);
                    };
                    _deviceWatcher.Start();
                }
            }
            catch (Exception ex)
            {
                StatusUpdated?.Invoke(this, $"藍芽音訊通道初始化錯誤: {ex.Message}");
            }
        }

        private async Task<bool> ConnectAudioDeviceAsync(string deviceId)
        {
            try
            {
                var connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
                if (connection != null)
                {
                    connection.Start();
                    var result = await connection.OpenAsync();
                    if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
                    {
                        lock (_activeConnections)
                        {
                            _activeConnections.Add(connection);
                        }
                        StatusUpdated?.Invoke(this, "藍芽音訊連線成功！Windows 已偽裝成耳機");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioSink] Connect Error: {ex.Message}");
            }
            return false;
        }

        private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession();
        }

        private void UpdateCurrentSession()
        {
            if (_manager == null) return;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            _currentSession = _manager.GetCurrentSession();

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                _ = RefreshMediaInfoAsync();
            }
            else
            {
                MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                {
                    Title = "已偽裝為耳機，等待播放...",
                    Artist = "請在手機點擊播放音樂",
                    Album = "",
                    Thumbnail = null,
                    IsPlaying = false
                });
            }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RefreshMediaInfoAsync();
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

        public async Task RefreshMediaInfoAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var mediaProps = await _currentSession.TryGetMediaPropertiesAsync();
                var playbackInfo = _currentSession.GetPlaybackInfo();

                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                BitmapImage? bitmap = null;
                if (mediaProps?.Thumbnail != null)
                {
                    try
                    {
                        using var stream = await mediaProps.Thumbnail.OpenReadAsync();
                        using var netStream = stream.AsStreamForRead();
                        var memStream = new MemoryStream();
                        await netStream.CopyToAsync(memStream);
                        memStream.Position = 0;

                        bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memStream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MediaManager] Thumbnail Load Error: {ex.Message}");
                    }
                }

                var args = new MediaInfoEventArgs
                {
                    Title = string.IsNullOrWhiteSpace(mediaProps?.Title) ? "未知曲目" : mediaProps.Title,
                    Artist = string.IsNullOrWhiteSpace(mediaProps?.Artist) ? "未知歌手" : mediaProps.Artist,
                    Album = mediaProps?.AlbumTitle ?? "",
                    Thumbnail = bitmap,
                    IsPlaying = isPlaying
                };

                MediaInfoUpdated?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaManager] Refresh Error: {ex.Message}");
            }
        }

        public async Task<bool> NextAsync()
        {
            if (_currentSession != null)
            {
                return await _currentSession.TrySkipNextAsync();
            }
            return false;
        }

        public async Task<bool> PreviousAsync()
        {
            if (_currentSession != null)
            {
                return await _currentSession.TrySkipPreviousAsync();
            }
            return false;
        }

        public async Task<bool> TogglePlayPauseAsync()
        {
            if (_currentSession != null)
            {
                return await _currentSession.TryTogglePlayPauseAsync();
            }
            return false;
        }
    }
}
