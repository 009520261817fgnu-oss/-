# WorldSim — 3D世界シミュレーション 試作版

仕様書: [docs/SPEC.md](docs/SPEC.md) ／ 設計: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) ／ タスクと進捗: [docs/TASKS.md](docs/TASKS.md)

現在の到達点: **フェーズ1（世界と基本操作）のコア部分**。地形生成・建物・道路・地形編集・時間管理は
Unity非依存のC#で実装し自動テスト済み。Unity側の表示・操作コードは書いてあるが、まだUnity上で実行確認していない。

## 構成

- `Packages/com.worldsim.core/` — シミュレーション本体（純C#、UnityEngine参照禁止）。Unityパッケージ形式。
- `UnityProject/Assets/WorldSim/` — Unityの表示・カメラ・入力・UI。
- `dotnet/` — Unityなしでコアをビルド・テスト・実行するための .NET プロジェクト。

## Unityなしで動かす（.NET 8 SDK）

```bash
cd dotnet
dotnet test                                   # 自動テスト
dotnet run --project WorldSim.Cli -- --seed 1 --days 30 --demo-edits --png ../out/map.png
```

CLIは世界を生成し、編集コマンドを試し、指定日数シミュレーションして、統計・状態ハッシュ・上から見た地図PNGを出力する。

## Unityで動かす（Unity 6 / URP）

1. Unity Hub で **Unity 6（6000.0 LTS 系）** の **Universal 3D** テンプレートから新規プロジェクトを作る。
2. `Window > Package Manager > + > Add package from disk...` で
   `Packages/com.worldsim.core/package.json` を選ぶ。
3. `UnityProject/Assets/WorldSim` フォルダーを、作ったプロジェクトの `Assets/` にコピーする。
4. `Project Settings > Player > Active Input Handling` が **Input System Package** または **Both** であることを確認する。
5. 任意のシーン（テンプレートの SampleScene でよい）で再生する。`WorldSim` オブジェクトが自動生成される。
6. テスト: プロジェクトの `Packages/manifest.json` に `"testables": ["com.worldsim.core"]` を追加すると、
   `Window > General > Test Runner > EditMode` で `WorldSim.Core.Tests` を実行できる。

操作: WASD/矢印=移動、ホイール=ズーム、Q/E・右ドラッグ=回転、C=カメラ切替、P=停止、1/2/3=速度、Esc=調査ツール。
左パネルからツールや建物を選び、左クリックで実行。タッチでは1本指=ツール、2本指=カメラ。

## 実装済み／未実装

実装済み・テスト済み（コア）: 地形生成、植生、建物の配置・削除、道路、地形の隆起・掘削・平坦化、
時間の停止・3段階加速、初期の町、建物・資源定義のJSON外部化、統計の日次記録、状態ハッシュ。

コードのみ・未検証（Unity）: 地形・海・建物・道路・樹木の3D表示、俯瞰/自由/一人称カメラ、ツール操作、HUD。

未実装: 住民、企業、生産、交通量、経済、技術、政治、環境、災害、セーブ／ロード、歴史分岐、グラフ。
