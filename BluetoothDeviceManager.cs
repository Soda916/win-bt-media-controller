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
        // Apple Media Service (AMS) 官方規範 UUID
        public static readonly Guid AmsServiceUuid = new("89D3502B-0F36-433A-8EF4-C502AD55F8DC");
        public static readonly Guid RemoteCommandCharUuid = new("9B3C81D8-57B1-4A8A-B8DF-0E56F7CA51C2");
        public static readonly Guid EntityUpdateCharUuid = new("2F7CABCE-808D-411F-9A0C-BB92BA96C102");
        public static readonly Guid EntityAttributeCharUuid = new("C6B2F38C-23AB-46D8-A6AB-A3A870BBD5D7");

        private BluetoothLEDevice? _connectedBleDevice;
        private GattCharacteristic? _remoteCommandChar;
        private GattCharacteristic? _entityUpdateChar;

        private string _title = "";
        private string _artist = "";
        private string _album = "";
        private bool _isPlaying = false;
        private bool _isConnecting = false;

        public event EventHandler<BleMediaInfoEventArgs>? OnMediaUpdated;
        public event EventHandler<string>? OnStatusMessage;

        public bool IsAmsConnected => _remoteCommandChar != null;
        public string ConnectedDeviceName => _connectedBleDevice?.Name ?? "";

        public async Task ScanAndConnectPairedDevicesAsync()
        {
            if (_isConnecting) return;
            _isConnecting = true;

            try
            {
                // 如果已經連線成功，避免重複查詢造成 AccessDenied
                if (IsAmsConnected && _entityUpdateChar != null)
                {
                    Logger.Log("[DeviceManager] AMS 已經處於連線狀態，主動重整曲目屬性...");
                    try
                    {
                        byte[] trackSub = new byte[] { 2, 0, 1, 2 };
                        await _entityUpdateChar.WriteValueAsync(trackSub.AsBuffer(), GattWriteOption.WriteWithResponse);
                        byte[] playerSub = new byte[] { 0, 1 };
                        await _entityUpdateChar.WriteValueAsync(playerSub.AsBuffer(), GattWriteOption.WriteWithResponse);
                    }
                    catch { }
                    return;
                }

                Logger.Log("[DeviceManager] 開始掃描系統中所有已配對的藍芽裝置...");

                string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var bleDevices = await DeviceInformation.FindAllAsync(bleSelector);
                Logger.Log($"[BLE] 找到 {bleDevices.Count} 個已配對 BLE 裝置:");
                foreach (var d in bleDevices)
                {
                    Logger.Log($"  - [BLE] 名稱: '{d.Name}', ID: {d.Id}");
                }

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

                OnStatusMessage?.Invoke(this, "未成功鎖定 AMS 特徵碼，請點擊重新整理...");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DeviceManager] 掃描異常: {ex}");
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task<bool> TryConnectAmsAsync(string deviceId)
        {
            try
            {
                var bleDev = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (bleDev != null)
                {
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
                Logger.Log($"[GATT] 正在讀取 '{bleDev.Name}' 的 GATT 服務...");

                var servicesResult = await bleDev.GetGattServicesAsync(BluetoothCacheMode.Cached);
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    servicesResult = await bleDev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                }

                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    Logger.Log($"[GATT] 查詢服務失敗: {servicesResult.Status}");
                    return false;
                }

                Logger.Log($"[GATT] '{bleDev.Name}' 共有 {servicesResult.Services.Count} 個 GATT 服務");

                // 【動態判定】比對 AMS UUID 或 開頭帶有 89d3502b 的服務
                var amsService = servicesResult.Services.FirstOrDefault(s =>
                    s.Uuid == AmsServiceUuid ||
                    s.Uuid.ToString().ToLower().StartsWith("89d3502b"));

                if (amsService == null)
                {
                    Logger.Log($"[GATT] '{bleDev.Name}' 未匹配到 AMS 服務 UUID");
                    return false;
                }

                Logger.Log($"[GATT] 🔥 動態鎖定 AMS 服務 ({amsService.Uuid})！獲取特徵碼中...");
                var charsResult = await amsService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
                if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                {
                    charsResult = await amsService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                }

                if (charsResult.Status != GattCommunicationStatus.Success)
                {
                    Logger.Log($"[GATT] 獲取特徵碼失敗: {charsResult.Status}");
                    return false;
                }

                foreach (var ch in charsResult.Characteristics)
                {
                    string uuidLower = ch.Uuid.ToString().ToLower();
                    Logger.Log($"  - 特徵碼 UUID: {ch.Uuid}");

                    // 【動態判定】比對 RemoteCommand 特徵碼
                    if (ch.Uuid == RemoteCommandCharUuid || uuidLower.StartsWith("9b3c81d8"))
                    {
                        _remoteCommandChar = ch;
                        Logger.Log("[GATT] ✅ 動態鎖定 RemoteCommand (控制指令)！");
                    }
                    // 【動態判定】比對 EntityUpdate 特徵碼
                    else if (ch.Uuid == EntityUpdateCharUuid || uuidLower.StartsWith("2f7cabce") || uuidLower.Contains("2f7c"))
                    {
                        _entityUpdateChar = ch;
                        Logger.Log("[GATT] ✅ 動態鎖定 EntityUpdate (曲目資料)！");
                    }
                }

                if (_entityUpdateChar != null)
                {
                    var notifyResult = await _entityUpdateChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    Logger.Log($"[GATT] 訂閱通知結果: {notifyResult}");

                    _entityUpdateChar.ValueChanged -= EntityUpdateChar_ValueChanged;
                    _entityUpdateChar.ValueChanged += EntityUpdateChar_ValueChanged;

                    // 訂閱 Track 屬性 (0: Artist, 1: Album, 2: Title)
                    byte[] trackSub = new byte[] { 2, 0, 1, 2 };
                    await _entityUpdateChar.WriteValueAsync(trackSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    // 訂閱 Player 狀態 (1: PlaybackInfo)
                    byte[] playerSub = new byte[] { 0, 1 };
                    await _entityUpdateChar.WriteValueAsync(playerSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    Logger.Log("[GATT] 🎉 AMS 屬性訂閱已發送！開始接收 iPhone 歌名！");
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
                            Logger.Log($"[AMS PlaybackState] 播放狀態解析為: {(_isPlaying ? "Playing" : "Paused")}");
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

        // 【普雷拋死徹底修復】
        // iOS AMS 對指令 2 (Toggle) 常有相容問題，改為明確發送 Play (0) 或 Pause (1)，並輔以 Toggle (2)
        public async Task<bool> TogglePlayPauseAsync()
        {
            byte explicitCmd = _isPlaying ? (byte)1 : (byte)0; // 播中發送 1 (Pause)，停中發送 0 (Play)
            Logger.Log($"[AMS Toggle] 目前 _isPlaying={_isPlaying}，優先發送命令 {explicitCmd} ({(explicitCmd == 1 ? "Pause" : "Play")})");

            bool ok = await SendCommandAsync(explicitCmd);
            if (!ok)
            {
                Logger.Log("[AMS Toggle] 明確命令失敗，備援發送命令 2 (TogglePlayPause)");
                ok = await SendCommandAsync(2);
            }
            return ok;
        }

        private async Task<bool> SendCommandAsync(byte cmdId)
        {
            if (_remoteCommandChar != null)
            {
                try
                {
                    byte[] payload = new byte[] { cmdId };
                    // 優先使用 WriteWithResponse 確保 iOS 確實驗收執行
                    var writeOption = _remoteCommandChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                        ? GattWriteOption.WriteWithoutResponse
                        : GattWriteOption.WriteWithResponse;

                    var res = await _remoteCommandChar.WriteValueAsync(payload.AsBuffer(), writeOption);
                    Logger.Log($"[AMS SendCommand] 指令 {cmdId} 發送 ({writeOption}) 結果: {res}");
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
