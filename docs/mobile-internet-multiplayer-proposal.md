# 行動版網際網路聯機：EOS

2026-10-08：使用者選擇改回 EOS。Unity Relay 實際服務回覆 HTTP 451 地區限制，詳見 [Unity 驗證紀錄](unity-relay-validation.md)。EOS 產品設定已保存到 GitHub，Client 憑證驗證取得 HTTP 200；Device ID 登入、大廳和強制中繼仍須實測。後續步驟與平台限制見 [EOS 設定與驗證](eos-setup.md)。

以下保留已實作的 Unity Relay 方案紀錄；目前遊戲尚未切換成 EOS。

## 玩家體驗

Android／iOS 玩家選擇 **ONLINE → INTERNET → HOST A BATTLE**，取得房間碼後分享給朋友。朋友選擇 **JOIN WITH CODE**，雙方選車、賽道、Ready，再由房主開始。

不需要相同 Wi-Fi、公網 IP、轉發路由器端口或由開發者維運伺服器。兩部手機透過 Unity 管理的 Relay 傳送資料；遊戲模擬仍在玩家裝置執行。玩家不需要登入 Unity 帳號，客戶端使用匿名 Authentication。

保留 **LAN**，供相同區域網路內使用。第一版不提供公開配對、好友名單或房主遷移。房主離開或連線中斷時沿用既有中斷處理，不把未完成比賽寫成勝負。

## 實作

- Unity `6000.6.4f1`、Multiplayer Services `2.3.3`、隨此 Editor 提供的 Unity Transport `6.6.0`，以 `Idas3UnityRelayTransport` 接上 `IIdas3Transport`。套件鎖定檔記錄實際解析版本。
- 使用 Relay allocation／join-code API，不需另外建立 Lobby。每個 allocation 只允許一名客人。
- 使用 DTLS 加密 Relay 路徑；不嘗試直接連接玩家 IP。
- 沿用原生物理、rollback、相容性握手、車庫、結果與再賽協定。
- 可靠封包使用 UTP 分片＋可靠有序 pipeline，最多 4,096 bytes。賽車輸入使用獨立分片 pipeline，不加可靠重傳；遊戲既有輸入歷史處理遺失封包。
- 排隊資料有上限。離房、逾時、切換 transport、Dispose 都會讓過期非同步結果失效。建立房間成功且 Relay bind 完成後才顯示房間碼。
- 只接受單一已接納 peer 的遊戲資料，再由 session 驗證完整遊戲版本與內容相容性。房間碼持有人可以嘗試加入，請只分享給朋友。
- `BytesSent`／`BytesReceived` 計算遊戲 payload，不包含 DTLS、Relay、IP 或重傳開銷，不能當成帳單用量。

## 費用與限制

Unity 官方當日價格：前 50 個**月平均同時在線人數（CCU）**免費；流量免費額度為每 CCU 3 GiB、每月最多 150 GiB。超額 CCU 為每個月平均 CCU US$0.16；超額流量美歐 US$0.09/GiB、亞洲／澳洲 US$0.16/GiB。以 Dashboard 與最新官方價目表為準。

150 GiB 是上限，不是每個低用量專案必定獲得的固定額度。未設定付款資料而超過免費額度時，Unity 可能阻止服務請求。Unity 引擎授權或 GitHub Pro 不包含不限量 Relay 流量。此程式不啟用付費方案。

DTLS／UDP 仍可能被特定網路阻擋。這一版不宣稱可通過所有電信商或網路限制，也尚未加入 WSS 備援。需要實測延遲、抖動、流量、斷線、背景切換與再賽。

## 設定與驗證

設定步驟見 [Unity Relay 設定](unity-relay-setup.md)。

本機 transport 測試以替代 allocation service 配合真正 UTP UDP sockets，檢查雙向資料、4 KiB 分片、有序交付、佇列、第三名玩家拒絕、取消／逾時與過期回呼。它不會向 Unity 申請 Relay，不代表已通過外網或電信商測試。

接上真實 Cloud Project 後，仍需驗證：Android 建房／iOS 加入、反向建房、完整完賽／再賽／離房、不相容版本拒絕，以及不同網路下的延遲與流量。Android／iOS 模擬器可先驗證真實 Relay 路徑，行動網路體驗最後需不同網路的實機。

## 官方資料

- [Unity Relay 整合](https://docs.unity.com/relay/integration)
- [Unity 6 Multiplayer Services 遷移](https://docs.unity.com/en-us/mps-sdk/migration-path)
- [Relay 協定](https://docs.unity.com/relay/networking)
- [UGS 價目表](https://unity.com/products/gaming-services/pricing)
- [免費額度與帳務](https://docs.unity.com/en-us/services/pricing-and-billing)
