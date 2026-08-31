# 盤古之白 PanguSpacing

在 Windows 上用一組快捷鍵，替選取的文字自動補上中英文之間的空白。

> 漢學家稱這個空白字元為「盤古之白」，因為它劈開了全形字和半形字之間的混沌。

```
我用C#寫了一個Windows程式    →    我用 C# 寫了一個 Windows 程式
```

## 特色

- **全域可用**：在任何程式裡都能用，Line、Discord、Chrome、Word、記事本都吃
- **常駐系統匣**：沒有主視窗，不佔工作列
- **不動剪貼簿**：轉換前後會自動備份還原，你原本複製的東西不會不見
- **保護網址與路徑**：`https://example.com/中文頁面` 不會被插入空白而失效
- **可復原**：不滿意直接按 <kbd>Ctrl</kbd>+<kbd>Z</kbd>

## 使用方式

1. 執行 `PanguSpacing.exe`，程式會縮到系統匣
2. 在任何程式裡**選取**要處理的文字
3. 按 <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>P</kbd>

選取的文字就會直接被替換成加好空白的版本。

右鍵點系統匣圖示還有兩個選項：

| 選項 | 用途 |
| --- | --- |
| 轉換選取的文字 | 跟快捷鍵一樣，不想記快捷鍵時用 |
| 只轉換剪貼簿內容 | 已經自己按過 <kbd>Ctrl</kbd>+<kbd>C</kbd>，只想處理剪貼簿、等一下自己貼 |

## 運作原理

Windows 沒有提供讀取其他程式選取文字的 API，所以本程式借道剪貼簿：

1. 備份目前的剪貼簿內容
2. 模擬送出 <kbd>Ctrl</kbd>+<kbd>C</kbd>，讓目標程式把選取的文字放進剪貼簿
3. 讀出文字並套用盤古規則
4. 寫回剪貼簿，模擬送出 <kbd>Ctrl</kbd>+<kbd>V</kbd>
5. 還原步驟 1 備份的內容

整個過程約 0.4 秒，不會有視窗閃過。

## 建置

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)，且只能在 Windows 上建置。

```bash
git clone https://github.com/AyaneChiang/PanguSpacing.git
cd PanguSpacing
dotnet run
```

發佈成單一執行檔：

```bash
dotnet publish -c Release
```

輸出在 `bin/Release/net10.0-windows/win-x64/publish/`。這是 framework-dependent 版本，目標電腦需要安裝 .NET 10 Desktop Runtime。若要做成免安裝執行環境的版本，把 `PanguSpacing.csproj` 裡的 `SelfContained` 改成 `true`。

## 開機自動啟動

把 `PanguSpacing.exe` 的捷徑放到啟動資料夾即可。按 <kbd>Win</kbd>+<kbd>R</kbd> 輸入：

```
shell:startup
```

## 已知限制

- **目標程式以系統管理員權限執行時無效**。這是 Windows 的 UIPI 保護機制，低權限程式無法對高權限視窗送出模擬按鍵。需要的話請讓兩者權限一致。
- 部分終端機、遊戲、密碼欄位不接受模擬按鍵。
- 只備份剪貼簿的純文字內容。若原本剪貼簿裡是圖片，轉換後不會被還原。
- 貼上後某些程式的復原紀錄可能不如預期。
- 防毒軟體可能因為全域熱鍵與 `SendInput` 的行為特徵而誤判，必要時請加入白名單。

若貼上後剪貼簿內容不正確，通常是目標程式讀取剪貼簿較慢。把 `Program.cs` 裡的 `await Task.Delay(250)` 調大到 400～500 即可。

## 轉換規則

在下列情況之間插入半形空白：

- 中日韓文字 ↔ 半形英文、數字
- 中日韓文字 ↔ 部分半形符號（`@ # $ % ^ & * - + / \ = |` 與括號、標點）

不處理的情況：

- 已經有空白的地方
- 全形標點前後
- 網址（`http://`、`https://`、`ftp://`）
- Windows 路徑（`C:\...`）與 UNC 路徑（`\\...`）

規則實作在 `Program.cs` 的 `Pangu` 類別，可依需求調整。

## 致謝

轉換規則參考 [vinta/pangu.js](https://github.com/vinta/pangu.js)。

## 授權

MIT
