using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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
    }

    public class MediaManager
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public event EventHandler<MediaInfoEventArgs>? MediaInfoUpdated;
        public event EventHandler<bool>? PlaybackStateUpdated;

        public async Task InitializeAsync()
        {
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
                System.Diagnostics.Debug.WriteLine($"[MediaManager] Init Error: {ex.Message}");
            }
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
                // 無正在播放的會話
                MediaInfoUpdated?.Invoke(this, new MediaInfoEventArgs
                {
                    Title = "等待藍芽/媒體連線...",
                    Artist = "請播放手機音樂",
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
                        bitmap.Freeze(); // 跨執行緒安全
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
