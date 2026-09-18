using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MediaController
{
    public class BleMediaInfoEventArgs : EventArgs
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public bool IsPlaying { get; set; }
    }

    public class BluetoothDeviceManager
    {
        // Apple Media Service (AMS)
        public static readonly Guid AmsServiceUuid = new("89D3502B-0F36-433A-82F9-C1322C2F8F00");
        public static readonly Guid RemoteCommandCharUuid = new("9B3C81D8-57B1-4A8A-B8DF-FB571277F1C9");
        public static readonly Guid EntityUpdateCharUuid = new("2F7DD41B-30AB-415B-9F12-9A21ECFA6A21");

        private BluetoothLEDevice? _connectedBleDevice;
        private GattCharacteristic? _remoteCommandChar;
        private GattCharacteristic? _entityUpdateChar;

        private string _title = "";
        private string _artist = "";
        private string _album = "";
        private bool _isPlaying = false;

        public event EventHandler<BleMediaInfoEventArgs>? OnMediaUpdated;
        public event EventHandler<string>? OnStatusMessage;

        public bool IsAmsConnected => _remoteCommandChar != null;
        public string ConnectedDeviceName => _connectedBleDevice?.Name ?? "";

        public async Task ScanAndConnectPairedDevicesAsync()
        {
            Logger.Log("[DeviceManager] 開始掃描系統中所有已配對的藍芽裝置...");

            try
            {
                // 1. 查詢 BLE 已配對清單
                string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var bleDevices = await DeviceInformation.FindAllAsync(bleSelector);
                Logger.Log($"[BLE] 找到 {bleDevices.Count} 個已配對 BLE 裝置:");
                foreach (var d in bleDevices)
                {
                    Logger.Log($"  - [BLE] 名稱: '{d.Name}', ID: {d.Id}");
                }

                // 2. 優先連線已配對的 BLE 裝置 (使用 Cached 模式，秒讀服務清單)
                foreach (var d in bleDevices)
                {
                    Logger.Log($"[BLE] 正在嘗試快速連線已配對裝置: '{d.Name}'...");
                    bool ok = await TryConnectAmsAsync(d.Id);
                    if (ok)
                    {
                        Logger.Log($"[BLE] 成功與 '{d.Name}' 建立 AMS 媒體通道！");
                        OnStatusMessage?.Invoke(this, $"已連線 iPhone: {d.Name}");
                        return;
                    }
                }

                // 3. 查詢經典藍芽已配對清單以記錄診斷資訊
                string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var classicDevices = await DeviceInformation.FindAllAsync(classicSelector);
                Logger.Log($"[ClassicBT] 找到 {classicDevices.Count} 個已配對經典藍芽裝置:");
                foreach (var d in classicDevices)
                {
                    Logger.Log($"  - [Classic] 名稱: '{d.Name}', ID: {d.Id}");
                }

                OnStatusMessage?.Invoke(this, "請在 iPhone 藍芽設定點擊電腦名稱連線...");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DeviceManager] 掃描異常: {ex}");
            }
        }

        private async Task<bool> TryConnectAmsAsync(string deviceId)
        {
            try
            {
                var bleDev = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (bleDev != null)
                {
                    // 監聽連線狀態改變
                    bleDev.ConnectionStatusChanged += BleDev_ConnectionStatusChanged;
                    return await SetupAmsGattAsync(bleDev);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TryConnectAms] 異常: {ex.Message}");
            }
            return false;
        }

        private async void BleDev_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            Logger.Log($"[ConnectionStatusChanged] '{sender.Name}' 連線狀態變更: {sender.ConnectionStatus}");
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected && !IsAmsConnected)
            {
                Logger.Log("[ConnectionStatusChanged] 偵測到手機連線！重新初始化 AMS...");
                await SetupAmsGattAsync(sender);
            }
            else if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                _remoteCommandChar = null;
                _entityUpdateChar = null;
                OnStatusMessage?.Invoke(this, "iPhone 已中斷連線，等候重連...");
            }
        }

        private async Task<bool> SetupAmsGattAsync(BluetoothLEDevice bleDev)
        {
            try
            {
                _connectedBleDevice = bleDev;
                Logger.Log($"[GATT] 正在讀取 '{bleDev.Name}' 的本地快取 GATT 服務 (Cached)...");

                // 使用 Cached 模式秒讀，不再空中阻塞 8 秒！
                var servicesResult = await bleDev.GetGattServicesAsync(BluetoothCacheMode.Cached);
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    Logger.Log($"[GATT] 本地快取無服務 (Status={servicesResult.Status})，嘗試快速空中查詢...");
                    servicesResult = await bleDev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                }

                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    Logger.Log($"[GATT] 查詢服務失敗: {servicesResult.Status}");
                    return false;
                }

                Logger.Log($"[GATT] '{bleDev.Name}' 共有 {servicesResult.Services.Count} 個 GATT 服務");
                foreach (var s in servicesResult.Services)
                {
                    Logger.Log($"  - 服務 UUID: {s.Uuid}");
                }

                var amsService = servicesResult.Services.FirstOrDefault(s => s.Uuid == AmsServiceUuid);
                if (amsService == null)
                {
                    Logger.Log($"[GATT] '{bleDev.Name}' 服務清單中未見 AMS UUID，等候裝置完全握手...");
                    return false;
                }

                Logger.Log("[GATT] 成功鎖定 Apple Media Service (AMS)！獲取特徵碼中...");
                var charsResult = await amsService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
                if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                {
                    charsResult = await amsService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                }

                if (charsResult.Status != GattCommunicationStatus.Success) return false;

                foreach (var ch in charsResult.Characteristics)
                {
                    if (ch.Uuid == RemoteCommandCharUuid)
                    {
                        _remoteCommandChar = ch;
                        Logger.Log("[GATT] 成功獲取 RemoteCommand 特徵碼！");
                    }
                    else if (ch.Uuid == EntityUpdateCharUuid)
                    {
                        _entityUpdateChar = ch;
                        Logger.Log("[GATT] 成功獲取 EntityUpdate 特徵碼！");
                    }
                }

                if (_entityUpdateChar != null)
                {
                    // 訂閱通知
                    var notifyResult = await _entityUpdateChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    Logger.Log($"[GATT] 訂閱通知結果: {notifyResult}");

                    _entityUpdateChar.ValueChanged -= EntityUpdateChar_ValueChanged;
                    _entityUpdateChar.ValueChanged += EntityUpdateChar_ValueChanged;

                    // 訂閱 Track 資訊 (Artist, Album, Title)
                    byte[] trackSub = new byte[] { 2, 0, 1, 2 };
                    await _entityUpdateChar.WriteValueAsync(trackSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    // 訂閱 Player 狀態 (PlaybackInfo)
                    byte[] playerSub = new byte[] { 0, 1 };
                    await _entityUpdateChar.WriteValueAsync(playerSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    Logger.Log("[GATT] AMS 屬性訂閱發送完成！已準備接收歌名");
                    OnStatusMessage?.Invoke(this, $"AMS 已連線: {bleDev.Name}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SetupAmsGatt] 異常: {ex.Message}");
            }
            return false;
        }

        private void EntityUpdateChar_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                byte[] data = args.CharacteristicValue.ToArray();
                if (data.Length < 3) return;

                byte entityId = data[0];
                byte attrId = data[1];
                string val = data.Length > 3 ? Encoding.UTF8.GetString(data, 3, data.Length - 3) : "";

                Logger.Log($"[AMS Track Data] Entity={entityId}, Attr={attrId}, Value='{val}'");

                if (entityId == 2) // Track
                {
                    switch (attrId)
                    {
                        case 0: _artist = val; break;
                        case 1: _album = val; break;
                        case 2: _title = val; break;
                    }
                    TriggerMediaUpdated();
                }
                else if (entityId == 0 && attrId == 1) // PlaybackInfo
                {
                    if (!string.IsNullOrEmpty(val))
                    {
                        var parts = val.Split(',');
                        if (parts.Length > 0 && int.TryParse(parts[0], out int st))
                        {
                            _isPlaying = (st == 1);
                            TriggerMediaUpdated();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AMS ValueChanged] 解析錯誤: {ex.Message}");
            }
        }

        private void TriggerMediaUpdated()
        {
            OnMediaUpdated?.Invoke(this, new BleMediaInfoEventArgs
            {
                Title = string.IsNullOrWhiteSpace(_title) ? "未知曲目" : _title,
                Artist = string.IsNullOrWhiteSpace(_artist) ? "未知歌手" : _artist,
                Album = _album,
                IsPlaying = _isPlaying
            });
        }

        public async Task<bool> NextTrackAsync() => await SendCommandAsync(3);
        public async Task<bool> PreviousTrackAsync() => await SendCommandAsync(4);
        public async Task<bool> TogglePlayPauseAsync() => await SendCommandAsync(2);

        private async Task<bool> SendCommandAsync(byte cmdId)
        {
            if (_remoteCommandChar != null)
            {
                try
                {
                    byte[] payload = new byte[] { cmdId };
                    var res = await _remoteCommandChar.WriteValueAsync(payload.AsBuffer(), GattWriteOption.WriteWithoutResponse);
                    Logger.Log($"[AMS SendCommand] 指令 {cmdId} 發送結果: {res}");
                    return res == GattCommunicationStatus.Success;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[AMS SendCommand] 異常: {ex.Message}");
                }
            }
            return false;
        }
    }
}
