using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MediaController
{
    public class AmsMediaEventArgs : EventArgs
    {
        public string Title { get; set; } = "未知曲目";
        public string Artist { get; set; } = "未知歌手";
        public string Album { get; set; } = "";
        public bool IsPlaying { get; set; }
    }

    public class AmsBleManager
    {
        // Apple Media Service (AMS) UUIDs
        public static readonly Guid AmsServiceUuid = new("89D3502B-0F36-433A-82F9-C1322C2F8F00");
        public static readonly Guid RemoteCommandCharUuid = new("9B3C81D8-57B1-4A8A-B8DF-FB571277F1C9");
        public static readonly Guid EntityUpdateCharUuid = new("2F7DD41B-30AB-415B-9F12-9A21ECFA6A21");
        public static readonly Guid EntityAttributeCharUuid = new("C6B2F38C-23AB-4618-8835-732380C607A0");

        // Remote Command IDs
        public const byte CMD_PLAY = 0;
        public const byte CMD_PAUSE = 1;
        public const byte CMD_TOGGLE_PLAY_PAUSE = 2;
        public const byte CMD_NEXT_TRACK = 3;
        public const byte CMD_PREV_TRACK = 4;
        public const byte CMD_VOL_UP = 5;
        public const byte CMD_VOL_DOWN = 6;

        // Entity IDs
        public const byte ENTITY_ID_PLAYER = 0;
        public const byte ENTITY_ID_QUEUE = 1;
        public const byte ENTITY_ID_TRACK = 2;

        // Track Attribute IDs
        public const byte TRACK_ATTR_ARTIST = 0;
        public const byte TRACK_ATTR_ALBUM = 1;
        public const byte TRACK_ATTR_TITLE = 2;
        public const byte TRACK_ATTR_DURATION = 3;

        private BluetoothLEDevice? _device;
        private GattCharacteristic? _remoteCommandChar;
        private GattCharacteristic? _entityUpdateChar;
        private GattCharacteristic? _entityAttributeChar;
        private BluetoothLEAdvertisementWatcher? _bleWatcher;

        private string _currentTitle = "未知曲目";
        private string _currentArtist = "未知歌手";
        private string _currentAlbum = "";
        private bool _isPlaying = false;

        public event EventHandler<AmsMediaEventArgs>? MediaInfoChanged;
        public event EventHandler<string>? StatusChanged;
        public bool IsConnected => _remoteCommandChar != null;

        public async Task StartAsync()
        {
            StatusChanged?.Invoke(this, "搜尋已配對的 iPhone 藍芽裝置...");

            // 1. 先從系統已配對的 BLE 裝置中尋找
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var pairedDevices = await DeviceInformation.FindAllAsync(selector);

            foreach (var devInfo in pairedDevices)
            {
                if (await TryConnectDeviceAsync(devInfo.Id))
                    return;
            }

            // 2. 如果沒連上，開啟 BLE 廣播監聽器等候 iPhone 連入
            _bleWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _bleWatcher.Received += BleWatcher_Received;
            _bleWatcher.Start();
            StatusChanged?.Invoke(this, "等待 iPhone BLE 廣播...");
        }

        private async void BleWatcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (IsConnected) return;

            // 檢查是否帶有 AMS Service UUID
            if (args.Advertisement.ServiceUuids.Contains(AmsServiceUuid))
            {
                _bleWatcher?.Stop();
                var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                if (dev != null)
                {
                    await ConnectGattAsync(dev);
                }
            }
        }

        public async Task<bool> TryConnectDeviceAsync(string deviceId)
        {
            try
            {
                var dev = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (dev != null)
                {
                    return await ConnectGattAsync(dev);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AMS] Connect failed: {ex.Message}");
            }
            return false;
        }

        private async Task<bool> ConnectGattAsync(BluetoothLEDevice dev)
        {
            try
            {
                _device = dev;
                _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

                var gattResult = await _device.GetGattServicesForUuidAsync(AmsServiceUuid, BluetoothCacheMode.Uncached);
                if (gattResult.Status != GattCommunicationStatus.Success || !gattResult.Services.Any())
                {
                    // 嘗試從全部服務中找尋
                    var allServices = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                    var amsService = allServices.Services.FirstOrDefault(s => s.Uuid == AmsServiceUuid);
                    if (amsService == null) return false;
                    return await SetupCharacteristicsAsync(amsService);
                }

                return await SetupCharacteristicsAsync(gattResult.Services[0]);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"連線錯誤: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetupCharacteristicsAsync(GattDeviceService service)
        {
            try
            {
                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status != GattCommunicationStatus.Success) return false;

                foreach (var ch in charsResult.Characteristics)
                {
                    if (ch.Uuid == RemoteCommandCharUuid)
                        _remoteCommandChar = ch;
                    else if (ch.Uuid == EntityUpdateCharUuid)
                        _entityUpdateChar = ch;
                    else if (ch.Uuid == EntityAttributeCharUuid)
                        _entityAttributeChar = ch;
                }

                if (_entityUpdateChar != null)
                {
                    // 訂閱 Entity Update 通知
                    await _entityUpdateChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    _entityUpdateChar.ValueChanged += EntityUpdateChar_ValueChanged;

                    // 發送訂閱實體屬性命令 (Track: Artist, Album, Title)
                    byte[] trackSub = new byte[] { ENTITY_ID_TRACK, TRACK_ATTR_ARTIST, TRACK_ATTR_ALBUM, TRACK_ATTR_TITLE };
                    await _entityUpdateChar.WriteValueAsync(trackSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    // 訂閱 Player 狀態 (PlaybackInfo)
                    byte[] playerSub = new byte[] { ENTITY_ID_PLAYER, 1 }; // PlaybackInfo
                    await _entityUpdateChar.WriteValueAsync(playerSub.AsBuffer(), GattWriteOption.WriteWithResponse);
                }

                StatusChanged?.Invoke(this, $"AMS 藍芽已連線: {_device?.Name ?? "iPhone"}");
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"AMS 設定錯誤: {ex.Message}");
                return false;
            }
        }

        private void EntityUpdateChar_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                byte[] data = args.CharacteristicValue.ToArray();
                if (data.Length < 3) return;

                byte entityId = data[0];
                byte attrId = data[1];
                byte flags = data[2];

                string value = data.Length > 3 ? Encoding.UTF8.GetString(data, 3, data.Length - 3) : "";

                if (entityId == ENTITY_ID_TRACK)
                {
                    switch (attrId)
                    {
                        case TRACK_ATTR_TITLE:
                            _currentTitle = string.IsNullOrWhiteSpace(value) ? "未知曲目" : value;
                            break;
                        case TRACK_ATTR_ARTIST:
                            _currentArtist = string.IsNullOrWhiteSpace(value) ? "未知歌手" : value;
                            break;
                        case TRACK_ATTR_ALBUM:
                            _currentAlbum = value;
                            break;
                    }

                    NotifyMediaChanged();
                }
                else if (entityId == ENTITY_ID_PLAYER && attrId == 1) // PlaybackInfo
                {
                    // Format: "PlaybackState,PlaybackRate,ElapsedTime"
                    if (!string.IsNullOrEmpty(value))
                    {
                        var parts = value.Split(',');
                        if (parts.Length > 0 && int.TryParse(parts[0], out int state))
                        {
                            // 1 = Playing, 0 = Paused, 2 = Rewinding, 3 = FastForwarding
                            _isPlaying = (state == 1);
                            NotifyMediaChanged();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AMS] Parse error: {ex.Message}");
            }
        }

        private void NotifyMediaChanged()
        {
            MediaInfoChanged?.Invoke(this, new AmsMediaEventArgs
            {
                Title = _currentTitle,
                Artist = _currentArtist,
                Album = _currentAlbum,
                IsPlaying = _isPlaying
            });
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                _remoteCommandChar = null;
                StatusChanged?.Invoke(this, "iPhone 藍芽連線已斷開，嘗試重連中...");
            }
        }

        public async Task<bool> SendCommandAsync(byte commandId)
        {
            if (_remoteCommandChar != null)
            {
                try
                {
                    byte[] payload = new byte[] { commandId };
                    var result = await _remoteCommandChar.WriteValueAsync(payload.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                    return result == GattCommunicationStatus.Success;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AMS] Send cmd error: {ex.Message}");
                }
            }
            return false;
        }

        public async Task NextTrackAsync() => await SendCommandAsync(CMD_NEXT_TRACK);
        public async Task PreviousTrackAsync() => await SendCommandAsync(CMD_PREV_TRACK);
        public async Task TogglePlayPauseAsync() => await SendCommandAsync(CMD_TOGGLE_PLAY_PAUSE);
        public async Task VolumeUpAsync() => await SendCommandAsync(CMD_VOL_UP);
        public async Task VolumeDownAsync() => await SendCommandAsync(CMD_VOL_DOWN);
    }
}
