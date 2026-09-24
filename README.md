# 🎵 Windows Bluetooth Media Controller (for iOS & Bluetooth Audio)

**Windows 藍芽媒體控制器**。
免安裝、隨插即用單一執行檔（Self-contained `.exe`），支援 iPhone 藍芽媒體資訊抓取（歌名、歌手、專輯封面）與背景全域快捷鍵盲操切歌。

---

## ✨ 功能亮點

1. **即插即用（Zero Installation）**：
   * 單一 `.exe` 綠色免安裝，隨身碟點開直接跑。
   * 不需要電腦系統管理員 (Admin) 權限。
2. **iPhone 媒體資訊即時抓取**：
   * 透過 Windows 10/11 原生 WinRT GSMTC 媒體控制架構。
   * 即時同步：**歌曲名稱、歌手、專輯、專輯封面圖、播放/暫停狀態**。
3. **解題背景盲操（全域快捷鍵）**：
   * 視窗在背景、解題畫面全螢幕也能直接切歌：
     * `Ctrl + Alt + →`：下一首 (Next)
     * `Ctrl + Alt + ←`：上一首 (Previous)
     * `Ctrl + Alt + Space`：播放 / 暫停 (Play / Pause)
4. **極簡懸浮窗 (Floating Widget)**：
   * 支援任意拖曳移動。
   * 可釘選置頂 (`📌`) 或縮小至工作列。

---

## 🚀 使用步驟（3 步驟搞定）

### 步驟 1：iPhone 與電腦藍芽配對
1. 打開 Windows「設定」➔「藍芽與其他裝置」➔「新增裝置」。
2. iPhone 開啟藍芽並點選電腦進行配對（只需配對一次）。
3. 手機播放音樂，確認耳機音訊正常。

### 步驟 2：執行程式
* 將 `MediaController.exe` 放入隨身碟，在電腦上雙擊打開。
* 右下角即會浮現迷你播放器，並自動載入 iPhone 正在播放的曲目與封面。

### 步驟 3：解題盲操
* 直接使用鍵盤快捷鍵切歌，手不用離開鍵盤！

---

## 🛠️ 編譯方式

### 方式 A：GitHub Actions 自動雲端編譯（推薦）
每次 Push 到 GitHub Repo 或新增 Tag，GitHub 的雲端 Windows 伺服器會自動編譯並在 **Actions -> Artifacts** 產生最新的 `MediaController.exe`，直接下載即可。

### 方式 B：Windows 本地編譯
在安裝有 .NET 8 SDK 的 Windows 機器上執行：
```cmd
dotnet publish MediaController.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```
或直接雙擊 `build.bat`。

---

## ⚠️ 常見問題 (FAQ)

* **Q: Windows 跳出 SmartScreen 藍色警告「Windows 已保護您的電腦」？**
  * **A**: 點擊「其他資訊 (More info)」➔「仍要執行 (Run anyway)」即可正常啟動。
* **Q: 聲音會不會被搶走？**
  * **A**: 若使用支援一拖二的多點藍芽耳機，耳機連著電腦，iPhone 聲音透過電腦串流給耳機，控制與聽歌一氣呵成。
