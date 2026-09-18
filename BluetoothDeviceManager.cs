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
        // Apple Media Service (AMS) 官方規範 128-bit UUIDs
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

        public bool IsAmsConnected => _remoteCommandChar != null && _entityUpdateChar != null;
        public string ConnectedDeviceName => _connectedBleDevice?.Name ?? "";

        public async Task ScanAndConnectPairedDevicesAsync()
        {
            if (_isConnecting) return;
            _isConnecting = true;

            try
            {
                // 若已完整連線，只發送刷新屬性，不重複握手以防 AccessDenied
                if (IsAmsConnected && _entityUpdateChar != null)
                {
                    Logger.Log("[DeviceManager] AMS 已經完整就緒，重新發送曲目屬性訂閱...");
                    try
                    {
                        byte[] trackSub = new byte[] { 2, 0, 1, 2 };
                        await _entityUpdateChar.WriteValueAsync(trackSub.AsBuffer(), GattWriteOption.WriteWithResponse);
                        byte[] playerSub = new byte[] { 0, 1 };
                        await _entityUpdateChar.WriteValueAsync(playerSub.AsBuffer(), GattWriteOption.WriteWithResponse);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[DeviceManager] 刷新訂閱異常: {ex.Message}，重設連線狀態");
                        _remoteCommandChar = null;
                        _entityUpdateChar = null;
                    }
                    return;
                }

                Logger.Log("[DeviceManager] 開始掃描已配對 BLE 裝置...");

                string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var bleDevices = await DeviceInformation.FindAllAsync(bleSelector);
                Logger.Log($"[BLE] 找到 {bleDevices.Count} 個已配對 BLE 裝置:");
                foreach (var d in bleDevices)
                {
                    Logger.Log($"  - [BLE] 名稱: '{d.Name}', ID: {d.Id}");
                }

                foreach (var d in bleDevices)
                {
                    Logger.Log($"[BLE] 嘗試連線已配對裝置: '{d.Name}'...");
                    bool ok = await TryConnectAmsAsync(d.Id);
                    if (ok)
                    {
                        Logger.Log($"[BLE] ✅ 成功與 '{d.Name}' 建立 AMS 完整通道！");
                        OnStatusMessage?.Invoke(this, $"已連線: {d.Name}");
                        return;
                    }
                }

                OnStatusMessage?.Invoke(this, "請在 iPhone 藍芽設定點擊電腦名稱連線...");
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
                    bleDev.ConnectionStatusChanged -= BleDev_ConnectionStatusChanged;
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
            Logger.Log($"[ConnectionStatusChanged] '{sender.Name}' 連線狀態: {sender.ConnectionStatus}");
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected && !IsAmsConnected)
            {
                Logger.Log("[ConnectionStatusChanged] 手機連上！啟動 AMS 握手...");
                await Task.Delay(300); // 緩衝 300ms 等待藍芽實體層穩定
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
            // 帶重試機制 (最多 3 次)，解決藍芽連線切換瞬間的 OperationCanceledException
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    _connectedBleDevice = bleDev;
                    Logger.Log($"[GATT] 讀取 '{bleDev.Name}' 服務 (嘗試 {attempt}/3)...");

                    var servicesResult = await bleDev.GetGattServicesAsync(BluetoothCacheMode.Cached);
                    if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                    {
                        servicesResult = await bleDev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                    }

                    if (servicesResult.Status != GattCommunicationStatus.Success)
                    {
                        Logger.Log($"[GATT] 查詢服務失敗: {servicesResult.Status}");
                        await Task.Delay(500);
                        continue;
                    }

                    // 動態匹配 AMS 服務 UUID
                    var amsService = servicesResult.Services.FirstOrDefault(s =>
                        s.Uuid == AmsServiceUuid ||
                        s.Uuid.ToString().ToLower().StartsWith("89d3502b"));

                    if (amsService == null)
                    {
                        Logger.Log($"[GATT] '{bleDev.Name}' 未見 AMS 服務 UUID");
                        return false;
                    }

                    Logger.Log($"[GATT] 🔥 鎖定 AMS 服務 ({amsService.Uuid})，獲取特徵碼...");
                    var charsResult = await amsService.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
                    if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
                    {
                        charsResult = await amsService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    }

                    if (charsResult.Status != GattCommunicationStatus.Success)
                    {
                        Logger.Log($"[GATT] 獲取特徵碼失敗: {charsResult.Status}");
                        await Task.Delay(500);
                        continue;
                    }

                    GattCharacteristic? rcChar = null;
                    GattCharacteristic? euChar = null;

                    foreach (var ch in charsResult.Characteristics)
                    {
                        string uuidLower = ch.Uuid.ToString().ToLower();
                        if (ch.Uuid == RemoteCommandCharUuid || uuidLower.StartsWith("9b3c81d8"))
                        {
                            rcChar = ch;
                        }
                        else if (ch.Uuid == EntityUpdateCharUuid || uuidLower.StartsWith("2f7cabce") || uuidLower.Contains("2f7c"))
                        {
                            euChar = ch;
                        }
                    }

                    if (rcChar == null || euChar == null)
                    {
                        Logger.Log("[GATT] 未找齊 RemoteCommand 或 EntityUpdate 特徵碼");
                        await Task.Delay(500);
                        continue;
                    }

                    // 1. 【關鍵修復】訂閱 RemoteCommand 通知！
                    // Apple AMS 規範明訂：Media Controller 必須先訂閱 RemoteCommand 通知，iOS 才會開放接受控制指令！
                    var rcNotify = await rcChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    Logger.Log($"[GATT] ✅ RemoteCommand 啟用控制通道通知: {rcNotify}");

                    // 2. 訂閱 EntityUpdate 通知 (接收曲目與進度)
                    var euNotify = await euChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    Logger.Log($"[GATT] ✅ EntityUpdate 啟用曲目通知: {euNotify}");

                    euChar.ValueChanged -= EntityUpdateChar_ValueChanged;
                    euChar.ValueChanged += EntityUpdateChar_ValueChanged;

                    // 3. 發送屬性訂閱 (0: Artist, 1: Album, 2: Title) & Player 狀態
                    byte[] trackSub = new byte[] { 2, 0, 1, 2 };
                    await euChar.WriteValueAsync(trackSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    byte[] playerSub = new byte[] { 0, 1 };
                    await euChar.WriteValueAsync(playerSub.AsBuffer(), GattWriteOption.WriteWithResponse);

                    // 4. 全部握手成功後，才正式賦值類別變數！
                    _remoteCommandChar = rcChar;
                    _entityUpdateChar = euChar;

                    Logger.Log("[GATT] 🎉 AMS 握手完全成功！控制與曲目同步全部就緒！");
                    OnStatusMessage?.Invoke(this, $"AMS 已連線: {bleDev.Name}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SetupAmsGatt] 嘗試 {attempt}/3 異常: {ex.Message}");
                    _remoteCommandChar = null;
                    _entityUpdateChar = null;
                    await Task.Delay(600);
                }
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
                            Logger.Log($"[AMS PlaybackState] 播放狀態更新為: {(_isPlaying ? "Playing" : "Paused")}");
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
        // 優先送 Toggle (2)，若無效補發明確的 Play (0) / Pause (1)
        public async Task<bool> TogglePlayPauseAsync()
        {
            Logger.Log($"[AMS Toggle] 發送 TogglePlayPause (2)...");
            bool ok = await SendCommandAsync(2);

            // 輔助雙重保險
            byte explicitCmd = _isPlaying ? (byte)1 : (byte)0;
            Logger.Log($"[AMS Toggle] 補發狀態命令: {explicitCmd} ({(explicitCmd == 1 ? "Pause" : "Play")})");
            await SendCommandAsync(explicitCmd);

            return ok;
        }

        private async Task<bool> SendCommandAsync(byte cmdId)
        {
            if (_remoteCommandChar != null)
            {
                try
                {
                    byte[] payload = new byte[] { cmdId };
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
