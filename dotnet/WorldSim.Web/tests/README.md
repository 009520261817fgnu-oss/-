# ブラウザ自動テスト

ヘッドレスChromiumでWeb版を実際に操作して確認します（人生体験モード、「なぜ？」パネル、分岐比較、スマホ幅の表示）。

```bash
cd dotnet && ./publish-web.sh            # out/web/wwwroot を作る
npm install playwright                    # 初回のみ（ブラウザ: npx playwright install chromium）
node WorldSim.Web/tests/life.test.js /tmp # 引数はスクリーンショットの保存先
node WorldSim.Web/tests/why.test.js /tmp
```

環境変数（任意）：
- `CHROMIUM`：Chromium実行ファイルのパス
- `PLAYWRIGHT_MODULE`：playwrightモジュールのパス
- `THREE_JS`：CDNに接続できない環境で使う three.min.js（r128）のローカルパス
- `WEB_ROOT`：公開物のフォルダ
- `EXPECT_HASH`：CLIの `--seed 7 --days 60` の状態ハッシュ。指定すると、ブラウザ版が同じ結果を出すか確かめる（AOTと通訳実行の一致の確認）
