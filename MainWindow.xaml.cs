using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MediaController
{
    public partial class MainWindow : Window
    {
        private readonly MediaManager _mediaManager = new();
        private readonly HotkeyManager _hotkeyManager = new();
        private bool _isPlaying = false;
        private bool _isPinned = true;
        private string _currentArtistText = "等待 iPhone 播放...";
        private DispatcherTimer? _statusResetTimer;
        private DateTime _lastRefreshTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            PositionWindowBottomRight();
        }

        private void PositionWindowBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 20;
            Top = workArea.Bottom - Height - 20;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Logger.Log("[UI] 視窗載入完成，註冊快捷鍵與事件...");

            // 初始化快捷鍵 (Ctrl+Alt+Left/Right/Space)
            _hotkeyManager.Initialize(this);
            _hotkeyManager.OnPreviousPressed += async () => await _mediaManager.PreviousAsync();
            _hotkeyManager.OnNextPressed += async () => await _mediaManager.NextAsync();
            _hotkeyManager.OnPlayPausePressed += async () => await _mediaManager.TogglePlayPauseAsync();

            // 監聽媒體更新
            _mediaManager.MediaInfoUpdated += MediaManager_MediaInfoUpdated;
            _mediaManager.PlaybackStateUpdated += MediaManager_PlaybackStateUpdated;
            _mediaManager.StatusUpdated += MediaManager_StatusUpdated;

            await _mediaManager.InitializeAsync();
        }

        // 【狀態/錯誤訊息不超過 3 秒，自動還原為歌手】
        private void MediaManager_StatusUpdated(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                TxtArtist.Text = status;

                _statusResetTimer?.Stop();
                _statusResetTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _statusResetTimer.Tick += (s, e) =>
                {
                    _statusResetTimer.Stop();
                    TxtArtist.Text = _currentArtistText;
                };
                _statusResetTimer.Start();
            });
        }

        private void MediaManager_MediaInfoUpdated(object? sender, MediaInfoEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                TxtTitle.Text = e.Title;
                _currentArtistText = string.IsNullOrEmpty(e.Album) ? e.Artist : $"{e.Artist} • {e.Album}";

                // 若目前沒有狀態計時器在倒數，立即顯示歌手
                if (_statusResetTimer == null || !_statusResetTimer.IsEnabled)
                {
                    TxtArtist.Text = _currentArtistText;
                }

                if (e.Thumbnail != null)
                {
                    ImgCover.Source = e.Thumbnail;
                    CoverPlaceholder.Visibility = Visibility.Collapsed;
                    ImgCover.Visibility = Visibility.Visible;
                }
                else
                {
                    ImgCover.Source = null;
                    CoverPlaceholder.Visibility = Visibility.Visible;
                    ImgCover.Visibility = Visibility.Collapsed;
                }

                UpdatePlayPauseButton(e.IsPlaying);
            });
        }

        private void MediaManager_PlaybackStateUpdated(object? sender, bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePlayPauseButton(isPlaying);
            });
        }

        private void UpdatePlayPauseButton(bool isPlaying)
        {
            _isPlaying = isPlaying;
            BtnPlayPause.Content = isPlaying ? "⏸" : "▶";
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            // 防連續手抖點擊 (Debounce 1.5 秒)
            if ((DateTime.Now - _lastRefreshTime).TotalMilliseconds < 1500)
            {
                return;
            }
            _lastRefreshTime = DateTime.Now;

            Logger.Log("[UI] 使用者手動點擊重新整理按鈕");
            _mediaManager.RefreshAllAsync();
        }

        private void MenuCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string log = Logger.GetLogText();
                Clipboard.SetText(log);
                MediaManager_StatusUpdated(this, "✅ Log 已複製到剪貼簿！");
                Logger.Log("[UI] 使用者複製了 Log 到剪貼簿");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"複製失敗: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MenuClearLog_Click(object sender, RoutedEventArgs e)
        {
            Logger.Clear();
            MediaManager_StatusUpdated(this, "Log 已清空");
        }

        private async void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            await _mediaManager.PreviousAsync();
        }

        private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaManager.TogglePlayPauseAsync();
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            await _mediaManager.NextAsync();
        }

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            Topmost = _isPinned;
            BtnPin.Foreground = _isPinned 
                ? (System.Windows.Media.Brush)FindResource("AccentColor") 
                : (System.Windows.Media.Brush)FindResource("TextSecondary");
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _statusResetTimer?.Stop();
            _hotkeyManager.Dispose();
        }
    }
}
