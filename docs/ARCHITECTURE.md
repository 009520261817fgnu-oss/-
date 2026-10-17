# アーキテクチャ設計（フェーズ0〜1時点）

## 1. 全体構成

```
WorldSim/
├─ Packages/com.worldsim.core/        Unity非依存の純C#パッケージ（UPM形式）
│   ├─ Runtime/                       asmdef: noEngineReferences = true（UnityEngine参照を禁止）
│   │   ├─ Common/      GridPos, DeterministicRandom, StateHasher, Json
│   │   ├─ Content/     定義データ（BuildingDef, ResourceDef）と ContentDatabase
│   │   ├─ World/       Terrain, TerrainGenerator, WorldState, Building, WorldChange
│   │   ├─ Commands/    プレイヤー操作 = IWorldCommand
│   │   ├─ Simulation/  SimClock, SimulationRunner, ISimulationSystem, StatsHistorySystem
│   │   └─ Bootstrap/   WorldFactory（初期世界の生成）
│   ├─ Content/Resources/WorldSimContent/*.json   外部化された定義データ
│   └─ Tests/Editor/                  NUnitテスト（Unity Test Runner と dotnet test の両方で実行）
├─ UnityProject/Assets/WorldSim/Scripts/   Presentation + UI（Unity依存、ここでは未検証）
├─ dotnet/                            .NET SDK 用ビルド（Core / Tests / Cli）
└─ docs/                              仕様書・設計・タスク
```

仕様5.2の層との対応:

| 層 | 実装場所 | 状態 |
|---|---|---|
| World | `Runtime/World` | 実装済み（地形・建物・道路・植生） |
| Simulation | `Runtime/Simulation` | 時間管理と統計のみ。住民・企業等はフェーズ2以降 |
| Presentation | `UnityProject/.../TerrainView, StructureView, CameraRig` | コードのみ・Unity未検証 |
| UI | `UnityProject/.../WorldSimHud, ToolController` | コードのみ・Unity未検証 |
| Persistence | — | フェーズ5（ハッシュ関数 `ComputeHash` のみ先行） |
| Content | `Runtime/Content` + JSON | 建物・資源の定義を外部化済み |
| Testing | `Tests/Editor` + `dotnet/` | 42件 |

**依存の方向は一方向**: Presentation/UI → Core。Core は描画・入力を一切知らない。

## 2. 世界データ

- **グリッド**: 1マス = 10m。初期世界は 128×128 マス（1.28km 四方）。X=東、Z=北。
- **地形**: マスの角（頂点）に高さを持つハイトフィールド `(W+1)×(D+1)`。
  南端が海、北が山、中央に都市用の平野。ノイズは自前実装（シード決定的）。
- **占有**: マスごとに建物ID（0=なし）と道路フラグ。建物と道路は重ならない。
- **植生**: マスごとの森林密度（0〜255）。建設・道路・水没で消える。フェーズ4で成長・伐採モデルへ拡張。
- **ID**: `WorldState.NextId` から単調増加。削除しても再利用しない（セーブ・参照の安定性）。
- **建物の高さ**: 足元の最高頂点を床面とし、基礎を最低頂点まで伸ばす。地形自体は変更しない。

## 3. 変更の流れ（コマンドとイベント）

```
入力(マウス/タッチ) → ToolController → IWorldCommand → WorldState.Try*() → 検証 → 変更
                                                                         ↓
                                         WorldState.Changed(WorldChange{種類, 範囲})
                                                                         ↓
                                 TerrainView / StructureView が dirty → LateUpdate で1回だけ再構築
```

- すべての編集はコマンド経由。検証ロジックは `WorldState` の1か所に集約。
  プレビュー（緑/赤のゴースト）も同じ `CanPlaceBuilding` を使うため、表示と実際の判定が一致する。
- コマンドは単純なデータなので、将来は記録・リプレイ・サーバー側検証（仕様5.5）に流用できる。
- 道路建設は原子的（途中に不可能なマスがあれば何も作らない）。
- 地形編集は、建物・道路が載っている頂点を保護する（浮いたり埋まったりしない）。

## 4. 時間管理

- 1 tick = ゲーム内1時間。1日 = 24 tick、1年 = 360日（簡易暦）。
- `SimulationRunner.Update(実時間秒)` が累積器から整数tickを実行。描画フレームとは独立（仕様5.2）。
- 速度: 停止 / 通常(2 tick/秒) / 高速(8) / 超高速(32)。
- **加速しても1 tickの処理は同一**なので、速度やフレームレートで結果が変わらない（テストで検証済み）。
- 1フレームの最大tick数を48に制限し、超過分は破棄して `DroppedTicks` として警告表示（停止しない）。
- システムは `IntervalTicks` ごとに登録順で実行（例: 統計は1日1回）。仕様5.3「更新頻度を現象ごとに分ける」に対応。

## 5. 決定性

- 乱数は `DeterministicRandom`（状態を保存可能）。`System.Random` は使わない。
- 反復順序が決まるコレクションのみ使用（建物は `SortedDictionary`）。
- `WorldState.ComputeHash()` で全状態の指紋を取り、同シード同結果をテストで保証。
- 制約: 浮動小数点を使うため、**異なるCPU/プラットフォーム間のビット一致は保証しない**。
  マルチプレイでロックステップ同期が必要になった場合は固定小数点化を検討する（現時点では不要）。

## 6. カメラと入力（Unity側）

| モード | PC | タッチ |
|---|---|---|
| 俯瞰 | WASD/矢印=移動、ホイール=ズーム、Q/E・右ドラッグ=回転、中ドラッグ=移動 | 2本指=移動/ピンチ/回転、1本指=移動（調査ツール時のみ） |
| 自由 | WASD、Space/Ctrl=上下、右ドラッグ=視点、Shift=高速 | 未対応（フェーズ6） |
| 一人称 | WASDで地表を歩行、右ドラッグ=視点 | 未対応（フェーズ6） |

- 入力は Unity Input System（新方式）。タッチは EnhancedTouch。1本指=ツール操作、2本指=カメラ。
- UIはIMGUIで仮実装（プレハブ不要、画面高に応じて拡大）。本格UIはUI Toolkitで作り直す予定。

## 7. 性能（測定値、.NET 8 / クラウドコンテナ）

- 128×128 世界の生成（地形・植生・町）: 約33ms
- 30日分（720 tick）のシミュレーション: 約2ms（現在は統計システムのみなので参考値）
- Unity側の描画性能は未測定。
