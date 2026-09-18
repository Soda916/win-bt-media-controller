using System;
using System.Windows;
using System.Windows.Input;

namespace MediaController
{
    public partial class MainWindow : Window
    {
        private readonly MediaManager _mediaManager = new();
        private readonly HotkeyManager _hotkeyManager = new();
        private bool _isPlaying = false;
        private bool _isPinned = true;

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
            // 初始化快捷鍵 (Ctrl+Alt+Left/Right/Space)
            _hotkeyManager.Initialize(this);
            _hotkeyManager.OnPreviousPressed += async () => await _mediaManager.PreviousAsync();
            _hotkeyManager.OnNextPressed += async () => await _mediaManager.NextAsync();
            _hotkeyManager.OnPlayPausePressed += async () => await _mediaManager.TogglePlayPauseAsync();

            // 監聽媒體更新
            _mediaManager.MediaInfoUpdated += MediaManager_MediaInfoUpdated;
            _mediaManager.PlaybackStateUpdated += MediaManager_PlaybackStateUpdated;

            await _mediaManager.InitializeAsync();
        }

        private void MediaManager_MediaInfoUpdated(object? sender, MediaInfoEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                TxtTitle.Text = e.Title;
                TxtArtist.Text = string.IsNullOrEmpty(e.Album) ? e.Artist : $"{e.Artist} • {e.Album}";

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
            _hotkeyManager.Dispose();
        }
    }
}
