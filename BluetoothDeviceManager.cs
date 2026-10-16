using System;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
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
        public double? Volume { get; set; }
        public double? Duration { get; set; }
        public double? ElapsedTime { get; set; }
        public double PlaybackRate { get; set; }
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
        private double? _volume;
        private double? _duration;
        private double? _elapsedTime;
        private double _playbackRate;
        private readonly SemaphoreSlim _scanGate = new(1, 1);
        private readonly SemaphoreSlim _setupGate = new(1, 1);
        private readonly object _stateLock = new();
        private CancellationTokenSource _connectionLifetime = new();
        private GattDeviceService? _amsService;

        public event EventHandler<BleMediaInfoEventArgs>? OnMediaUpdated;
        public event EventHandler<string>? OnStatusMessage;

        public bool IsAmsConnected => _remoteCommandChar != null && _entityUpdateChar != null;
        public string ConnectedDeviceName => _connectedBleDevice?.Name ?? "";

        public async Task ScanAndConnectPairedDevicesAsync()
        {
            if (!await _scanGate.WaitAsync(0)) return;
            try
            {
                if (IsAmsConnected) return;
                Logger.Log("[DeviceManager] 開始掃描已配對 BLE 裝置...");
                var devices = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
                Logger.Log($"[BLE] 找到 {devices.Count} 個已配對 BLE 裝置");
                foreach (var device in devices)
                {
                    BluetoothLEDevice? bleDev = null;
                    lock (_stateLock)
                    {
                        if (_connectedBleDevice?.DeviceId == device.Id) bleDev = _connectedBleDevice;
                    }
                    bleDev ??= await BluetoothLEDevice.FromIdAsync(device.Id);
                    if (bleDev == null) continue;
                    lock (_stateLock)
                    {
                        if (!ReferenceEquals(_connectedBleDevice, bleDev))
                        {
                            ResetConnection();
                            if (_connectedBleDevice != null)
                            {
                                _connectedBleDevice.ConnectionStatusChanged -= BleDev_ConnectionStatusChanged;
                                _connectedBleDevice.Dispose();
                            }
                            _connectedBleDevice = bleDev;
                            bleDev.ConnectionStatusChanged += BleDev_ConnectionStatusChanged;
                        }
                    }
                    if (await SetupAmsGattAsync(bleDev)) return;
                }
                if (!IsAmsConnected)
                    OnStatusMessage?.Invoke(this, "請在 iPhone 藍芽設定點擊電腦名稱連線...");
            }
            catch (Exception ex) { Logger.Log($"[DeviceManager] 掃描異常: {ex}"); }
            finally { _scanGate.Release(); }
        }

        // Called only while holding _stateLock. Each disconnect invalidates all old work.
        private void ResetConnection()
        {
            var previous = _connectionLifetime;
            _connectionLifetime = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            if (_entityUpdateChar != null)
                _entityUpdateChar.ValueChanged -= EntityUpdateChar_ValueChanged;
            _remoteCommandChar = null;
            _entityUpdateChar = null;
            _amsService?.Dispose();
            _amsService = null;
        }

        private async void BleDev_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            try
            {
                CancellationToken token;
                lock (_stateLock)
                {
                    if (!ReferenceEquals(sender, _connectedBleDevice)) return;
                    Logger.Log($"[ConnectionStatusChanged] '{sender.Name}' 連線狀態: {sender.ConnectionStatus}");
                    if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                    {
                        ResetConnection();
                        OnStatusMessage?.Invoke(this, "iPhone 已中斷連線，等候重連...");
                        return;
                    }
                    token = _connectionLifetime.Token;
                }
                await Task.Delay(300, token);
                await SetupAmsGattAsync(sender, token);
            }
            catch (OperationCanceledException) { Logger.Log("[GATT] 連線已變更，取消舊握手"); }
            catch (Exception ex) { Logger.Log($"[ConnectionStatusChanged] 異常: {ex}"); }
        }

        private async Task<T> GattStepAsync<T>(string name,
            Func<IAsyncOperation<T>> operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var elapsed = Stopwatch.StartNew();
            Logger.Log($"[GATT] {name} 開始");
            try
            {
                var result = await operation().AsTask(timeout.Token);
                Logger.Log($"[GATT] {name} 完成 ({elapsed.ElapsedMilliseconds}ms): {result}");
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"{name} 超過 15 秒 ({elapsed.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GATT] {name} 中止 ({elapsed.ElapsedMilliseconds}ms): {ex.Message}");
                throw;
            }
        }

        private static void RequireSuccess(GattCommunicationStatus status, string step)
        {
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"{step}: {status}");
        }

        private async Task<bool> SetupAmsGattAsync(BluetoothLEDevice bleDev,
            CancellationToken? expectedToken = null)
        {
            CancellationToken token;
            lock (_stateLock)
            {
                if (!ReferenceEquals(bleDev, _connectedBleDevice)) return false;
                token = expectedToken ?? _connectionLifetime.Token;
            }
            bool entered = false;
            try
            {
                await _setupGate.WaitAsync(token);
                entered = true;
                token.ThrowIfCancellationRequested();
                if (IsAmsConnected) return true;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    GattDeviceService? service = null;
                    GattCharacteristic? update = null;
                    try
                    {
                        Logger.Log($"[GATT] 讀取 '{bleDev.Name}' AMS 服務 (嘗試 {attempt}/3)...");
                        var services = await GattStepAsync("查詢 AMS 服務", () =>
                            bleDev.GetGattServicesForUuidAsync(AmsServiceUuid, BluetoothCacheMode.Uncached), token);
                        Logger.Log($"[GATT] AMS 服務結果: {services.Status}, 數量: {services.Services.Count}");
                        // Dispose even results returned just before a disconnect.
                        foreach (var candidate in services.Services)
                        {
                            if (service == null && candidate.Uuid == AmsServiceUuid) service = candidate;
                            else candidate.Dispose();
                        }
                        RequireSuccess(services.Status, "查詢 AMS 服務");
                        if (service == null) throw new InvalidOperationException("裝置未提供 AMS 服務");
                        var chars = await GattStepAsync("查詢 AMS 特徵碼", () =>
                            service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), token);
                        Logger.Log($"[GATT] AMS 特徵碼結果: {chars.Status}, 數量: {chars.Characteristics.Count}");
                        RequireSuccess(chars.Status, "查詢 AMS 特徵碼");
                        var remote = chars.Characteristics.FirstOrDefault(c => c.Uuid == RemoteCommandCharUuid);
                        update = chars.Characteristics.FirstOrDefault(c => c.Uuid == EntityUpdateCharUuid);
                        if (remote == null || update == null)
                            throw new InvalidOperationException("未找齊 AMS 特徵碼");

                        RequireSuccess(await GattStepAsync("RemoteCommand 通知訂閱", () =>
                            remote.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.Notify), token), "RemoteCommand 通知訂閱");
                        update.ValueChanged += EntityUpdateChar_ValueChanged;
                        RequireSuccess(await GattStepAsync("EntityUpdate 通知訂閱", () =>
                            update.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.Notify), token), "EntityUpdate 通知訂閱");
                        RequireSuccess(await GattStepAsync("Track 屬性訂閱", () =>
                            update.WriteValueAsync(new byte[] { 2, 0, 1, 2, 3 }.AsBuffer(),
                                GattWriteOption.WriteWithResponse), token), "Track 屬性訂閱");
                        RequireSuccess(await GattStepAsync("Player 屬性訂閱", () =>
                            update.WriteValueAsync(new byte[] { 0, 1, 2 }.AsBuffer(),
                                GattWriteOption.WriteWithResponse), token), "Player 屬性訂閱");
                        lock (_stateLock)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!ReferenceEquals(bleDev, _connectedBleDevice) ||
                                bleDev.ConnectionStatus != BluetoothConnectionStatus.Connected)
                                throw new OperationCanceledException("連線已失效");
                            _amsService = service;
                            _remoteCommandChar = remote;
                            _entityUpdateChar = update;
                            service = null; // Ownership transfers to the active connection.
                            update = null;
                            Logger.Log("[GATT] AMS 握手完全成功！");
                            OnStatusMessage?.Invoke(this, $"AMS 已連線: {bleDev.Name}");
                        }
                        return true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SetupAmsGatt] 嘗試 {attempt}/3 失敗: {ex}");
                    }
                    finally
                    {
                        if (update != null) update.ValueChanged -= EntityUpdateChar_ValueChanged;
                        service?.Dispose();
                    }
                    if (attempt < 3) await Task.Delay(600, token);
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                Logger.Log("[GATT] 連線已變更，取消舊握手");
                return false;
            }
            finally { if (entered) _setupGate.Release(); }
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
                        case 2:
                            // iOS 不保證 Track 屬性的通知順序；Duration (Attr 3)
                            // 經常會早於 Title (Attr 2) 抵達，不能在這裡清掉它。
                            if (!string.Equals(_title, val, StringComparison.Ordinal))
                            {
                                _elapsedTime = null;
                            }
                            _title = val;
                            break;
                        case 3:
                            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration))
                            {
                                _duration = Math.Max(0, duration);
                            }
                            break;
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
                            if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
                            {
                                _playbackRate = rate;
                            }
                            if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double elapsedTime))
                            {
                                _elapsedTime = Math.Max(0, elapsedTime);
                            }
                            Logger.Log($"[AMS PlaybackState] 播放狀態更新為: {(_isPlaying ? "Playing" : "Paused")}");
                            TriggerMediaUpdated();
                        }
                    }
                }
                else if (entityId == 0 && attrId == 2) // Volume (0.0～1.0)
                {
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double volume))
                    {
                        _volume = Math.Clamp(volume, 0, 1);
                        Logger.Log($"[AMS Volume] 手機音量更新為: {_volume:P0}");
                        TriggerMediaUpdated();
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
                IsPlaying = _isPlaying,
                Volume = _volume,
                Duration = _duration,
                ElapsedTime = _elapsedTime,
                PlaybackRate = _playbackRate
            });
        }

        public async Task<bool> NextTrackAsync() => await SendCommandAsync(3);
        public async Task<bool> PreviousTrackAsync() => await SendCommandAsync(4);
        public async Task<bool> VolumeUpAsync() => await SendCommandAsync(5);
        public async Task<bool> VolumeDownAsync() => await SendCommandAsync(6);

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
