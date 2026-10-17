# WorldSim — 都市・経済・個人の人生を統合する3D世界シミュレーション（開発中の試作版）

**目標**は、SimCity や Cities: Skylines II を、規模ではなく**シミュレーションの質**で超えることです。具体的には次の観点を重視します。
- 因果関係
- 自律性
- 一貫性
- 自由度
- 説明可能性
- 拡張性
- 実用的な性能

計画と判断事項は [docs/VISION_PLAN.md](docs/VISION_PLAN.md)、検証結果は [docs/CAUSAL_FINDINGS.md](docs/CAUSAL_FINDINGS.md) にあります。

> **このリポジトリについて**：プロジェクトは `worldsim` ブランチにあります。既定のブランチ（main）は無関係の別の内容です。
> リポジトリ名は `-`（ハイフン1文字）です。URL は `https://github.com/009520261817fgnu-oss/-/tree/worldsim` です。

## 現状（2026-10-17）

| 項目 | 状態 |
|---|---|
| シミュレーション本体 | 純C#（Unity非依存）、約8,200行 |
| 自動テスト | 158件（NUnit）、約2,800行。GitHub Actions で毎回実行 |
| 遊べる版 | ブラウザ版（Blazor WebAssembly＋Three.js）。同じC#を実行。PC・iPad・スマホ幅で自動テスト済み |
| Unity版 | 表示コードのみ。**Unity上での動作は未確認** |
| 10年連続実行 | 人口 約1,270人、失業率 約2%、1日あたり6.3ms、保存データ3.1MB（読込後も同じ未来になる） |

### 実装済み（自動テストで確認）

- **世界と建設**
  - 地形の生成と編集、建物・道路の建設と撤去。
  - 植生、天候、電力、大気汚染、洪水・地震と復旧。
- **経済**
  - 生産網（原料→中間財→製品）、在庫、物流、運賃、輸出入、市場価格。
  - 価格転嫁、雇用と賃金、税・補助・年金・失業手当、倒産と再開、配当。
- **住民**
  - 年齢・学歴・住まい・職場・通勤・買い物・満足度・健康。
  - 出生・死亡・転入・転出、世帯（夫婦・子ども・独立）。
  - 地価で決まる家賃と住まい選び、退去。
- **地元の産業**
  - 建設業（建設・維持・復旧を受注し、資材を調達）。
  - 企業向けサービス、診療所、公園。
- **自律する主体**
  - 民間投資家：輸入超過・品不足を見て開業する（理由を記録）。
  - 自治体の自動運営：政策を理由つきで調整する（任意）。
- **説明可能性**
  - すべての解雇・転入・倒産・価格変更・資金の移動を、理由つきで記録する。
  - 「なぜ？」パネルで、指標の変化を原因別に分解して示す。
  - 介入実験（因果プローブ）の自動テスト。
- **歴史分岐**：保存と読込、分岐、分岐の比較と「違いの原因」。
- **人生体験モード**
  - 住民ひとりとして暮らせる（歩行、建物の内部、仕事、買い物、食事、睡眠、職業訓練、転職、転居、会話、結婚）。
  - 世界の時間と経済は止まらず、同じ世界を共有する。
  - ジャイロ・マウスで視点を操作できる。

### 未実装（主なもの）

- 地域分割・複数都市・遠方の簡略計算（S5）
- 鉄道・港・橋
- 国家・外交
- 生態系
- 建物内部の自由な設計
- MODの読込
- マルチプレイ

一覧は [docs/VISION_PLAN.md](docs/VISION_PLAN.md) の2章、段階計画は5章にあります。

## 確認・再現の方法（.NET 8 SDK）

```bash
git clone -b worldsim https://github.com/009520261817fgnu-oss/-.git worldsim
cd worldsim/dotnet
dotnet test                                                              # 自動テスト158件
dotnet run -c Release --project WorldSim.Cli -- --days 60 --probe        # 介入実験の表（5通りの乱数で偶然を判別）
dotnet run -c Release --project WorldSim.Cli -- --days 3650 --save /tmp/w.json   # 10年連続実行と保存→読込の一致
./publish-web.sh                                                         # ブラウザ版を out/web/wwwroot に作る
```

ブラウザでの自動テストの手順は [dotnet/WorldSim.Web/tests/README.md](dotnet/WorldSim.Web/tests/README.md) にあります。

## 構成

```
Packages/com.worldsim.core/          シミュレーション本体（UnityEngine参照禁止、netstandard2.1）
  Runtime/World/                     地形・建物・道路・世界の状態
  Runtime/Economy/                   住民・企業・帳簿・原因の記録（CauseLog）
  Runtime/Systems/                   毎日のシステム（雇用・電力・生産・物流・小売・財務・人口・市場・交通・汚染・
                                     復旧・統計・投資家・自治体・健康・世帯・住宅市場）
  Runtime/Simulation/                固定刻みの実行器、因果プローブ、原因分解（Explainer）、検証器
  Runtime/People/                    人生体験モード（操作中の人物、1日の予定、生活のコマンド）
  Runtime/Persistence/               保存（v3、v1・v2も読める）と歴史分岐
  Runtime/Content/ + Content/Resources/WorldSimContent/*.json   建物・資源・レシピ・技術・内装の定義データ
  Tests/Editor/                      NUnit テスト
dotnet/                              .NET用のプロジェクト（Core・Tests・CLI・Web）
  WorldSim.Web/                      ブラウザ版（GameApi*.cs が窓口、wwwroot/app.js・life.js・why.js が表示と操作）
UnityProject/Assets/WorldSim/        Unity版の表示・入力（未検証）
docs/                                仕様・監査・計画・検証結果
.github/workflows/ci.yml             自動テスト（コア・介入実験・10年実行・ブラウザ）
```

依存関係の向き：表示層（Web・Unity・CLI）→ 実行者つきコマンド → 世界の状態 ← 各システム。本体から表示層への依存はありません。

## 資料

| 資料 | 内容 |
|---|---|
| [docs/VISION_PLAN.md](docs/VISION_PLAN.md) | 最上位目標、現状監査、不足一覧、構成、因果関係、段階計画（S1〜S10）、判断事項 |
| [docs/CAUSAL_FINDINGS.md](docs/CAUSAL_FINDINGS.md) | 介入実験の結果と、各段階で見つけた不具合・設計上の問題 |
| [docs/AUDIT.md](docs/AUDIT.md) | 最初の監査（確認済み・推測・未確認を区別） |
| [docs/LIFE_MODE.md](docs/LIFE_MODE.md) | 人生体験モードの設計と状況 |
| [docs/TASKS.md](docs/TASKS.md) | 段階ごとの進捗とテスト件数 |
| [docs/SPEC.md](docs/SPEC.md) | 元の仕様書と変更履歴 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 初期の設計メモ |

## Unityで動かす場合（PCが必要・未確認）

1. **Unity 6** の Universal 3D テンプレートで新規プロジェクトを作る。
2. Package Manager の「Add package from disk」で `Packages/com.worldsim.core/package.json` を追加する。
3. `UnityProject/Assets/WorldSim` を、作ったプロジェクトの `Assets/` にコピーする。
4. Active Input Handling を **Input System**（または Both）にする。

※ Unity版には `Packages/manifest.json` を含む完全なUnityプロジェクトはまだありません。
