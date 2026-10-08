# Unity Relay 設定與自動建置

Android 和 iOS 必須使用**同一個 Unity Cloud Project ID、同一個 environment 和相容的遊戲版本**。Unity Editor 授權登入不代表專案已連結 Cloud Project。

## 1. 建立 Cloud Project

1. 開啟 [Unity Dashboard](https://cloud.unity.com/)，登入自己的 Unity 帳號。
2. 選擇 Organization，建立此遊戲的 Project。
3. 在該 Project 的 **Gaming Services / Multiplayer / Relay** 啟用服務；Dashboard 分類名稱可能隨版本不同。
4. 確認 **Authentication** 可使用。第一版使用 anonymous sign-in，不需要 Google／Apple 登入服務金鑰。
5. 在 **Project Settings** 複製 **Project ID**（UUID），不是 Organization ID。
6. 在 **Environments** 確認 `production` 存在；如需隔離開發測試，可建立 `development`，但兩個平台必須指定相同名稱。

不需要把 Unity 密碼、access token、service-account key 放進遊戲。Relay 客戶端會取得短期登入與 allocation 資料。

## 2. 本機設定

也可在 Unity Editor 的 **Edit → Project Settings → Services** 連結同一個 Cloud Project。關閉 Editor 後，在倉庫根目錄執行（用真正 Project ID 取代佔位文字）：

```powershell
python "Tools/Configure Unity Services.py" --project-id "YOUR-PROJECT-UUID" --environment production
```

腳本設定 `ProjectSettings/ProjectSettings.asset` 的 `cloudProjectId`、啟用 Services 連結，並產生 Git 忽略的 `Assets/Resources/Idas3UnityServices.json`。它不啟用 Analytics／Ads。Project ID 是公開識別碼，不是密碼；上游原作者應使用自己的 Project，不要意外將 fork 的 Cloud Project 設定提交到上游。

未設定 Project ID 時仍可建置，單機與 LAN 可用；Internet 頁面顯示尚未設定。按 Host／Join 才會進行匿名登入與雲端請求，打開遊戲或使用 LAN 不會配置 Relay。

## 3. GitHub Actions

在 fork 開啟 **Settings → Secrets and variables → Actions → Variables**，新增 repository variables：

| Variable | 內容 |
| --- | --- |
| `UNITY_PROJECT_ID` | 真正 Project UUID |
| `UNITY_ENVIRONMENT` | 雙方共用環境名稱；未填預設 `production` |

原本 Unity Editor 授權用的 `UNITY_LICENSE`、`UNITY_EMAIL`、`UNITY_PASSWORD` 與 Android signing secrets 繼續保留；不要改成 Relay 設定。

`Mobile builds` 與 `Android APK` workflow 在 Unity 匯入之前執行同一份設定腳本。推送到 `mobile-ci` 或 `unity-relay` 分支、或建立 `mobile-v*` tag，會自動觸發 Android、iOS 模擬器和 iPhone 三個建置工作；其他工作分支可從 **Actions → Mobile builds → Run workflow** 手動選擇。未連結 Cloud Project 的 artifact 不會自動擁有 Internet 功能。

兩個平台會執行既有 mobile checks 和新增 UTP loopback checks。CI 不使用玩家帳號、不配置真實 Relay 房間。iPhone IPA 簽名與 Apple 開發者設定是獨立工作；Relay 並不取代簽名要求。

## 4. 真實聯機驗證

1. 建置／安裝同一 commit 的 Android 與 iOS 版本。
2. Android 開啟 **ONLINE → INTERNET → HOST A BATTLE**，等房間碼出現。
3. iOS 選擇 **INTERNET → JOIN WITH CODE**，輸入房間碼。
4. 雙方選車／賽道、Ready，房主開始，完成比賽，再測返回房間和再賽。
5. 交換房主平台再測一次。測試離房、建立中取消、錯誤房間碼、已滿房間，以及版本不同時的拒絕。
6. 模擬器成功後，用不同網路的手機測試，例如一部 Wi-Fi、另一部行動數據，再測不同電信商。
7. 查看 Dashboard Relay 用量，記錄實際 RTT／抖動與流量。payload counter 不含所有網路開銷，帳務以 Dashboard 為準。

房間碼在房主關閉、失去連線或 allocation 逾時後會失效，請重新建房。切到背景或鎖屏可能停止遊戲執行，第一版不提供無縫重連或房主遷移。

## 常見情況

| 現象 | 檢查 |
| --- | --- |
| Internet 顯示未設定 | 此安裝包是否在建置前設定真正 Project ID；設定 variable 後需要重新建置 |
| Unity sign-in failed | 網路、Cloud Project、Authentication、environment 是否正確 |
| Room code not found | 房主是否在線、代碼是否來自同一 Cloud Project 和 environment |
| 連線逾時 | Relay 是否啟用、用量／服務狀態、網路是否阻擋 UDP/DTLS |
| 已連線但拒絕開始 | 是否同一版本、相同賽道資料、雙方是否 Ready |

完成 Cloud Project 設定後才能驗證真實 Relay；本機 sockets 測試與既有 LAN 跨平台測試各自驗證不同部分。

已完成的檢查與目前限制見 [驗證紀錄](unity-relay-validation.md)。
