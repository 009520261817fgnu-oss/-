# WorldSim — 3D世界シミュレーション 試作版

仕様書: [docs/SPEC.md](docs/SPEC.md) ／ 設計: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) ／ 進捗: [docs/TASKS.md](docs/TASKS.md)

試作版のフェーズ0〜6を実装済み。建築・住民・企業・生産・交通・経済・技術・政策・環境・災害・保存・歴史分岐が
一つの世界で連動する。シミュレーション本体はUnity非依存のC#で、89件の自動テストで検証している。

## 遊び方（ブラウザ／iPad）

`dotnet/WorldSim.Web` がブラウザ版。同じC#コードがWebAssemblyで動き、Three.jsで3D表示する。

```bash
cd dotnet
./publish-web.sh   # out/web/wwwroot を任意の静的サーバーで配信（ランタイムは framework/ に配置）
```

- 移動：ドラッグ（調査ツール）／WASD、回転：右ドラッグ／2本指ひねり／Q・E、拡大：ホイール／ピンチ
- カメラ：俯瞰・自由・一人称（タッチではスティック表示）、時間：停止・1×・4×・16×（P／1／2／3）
- 右のパネル：情報・分析（原因診断と予測）・グラフ・政策・研究・歴史分岐・保存・災害・ニュース

## Unityなしで動かす（.NET 8 SDK）

```bash
cd dotnet
dotnet test                                                     # 自動テスト（89件）
dotnet run -c Release --project WorldSim.Cli -- --seed 1 --days 360 --profile --companies --png ../out/map.png
```

## Unityで動かす（Unity 6 / URP）※PC（Windows・Mac・Linux）が必要

1. Unity Hub で **Unity 6（6000.0 LTS 系）** の **Universal 3D** テンプレートから新規プロジェクトを作る。
2. `Window > Package Manager > + > Add package from disk...` で `Packages/com.worldsim.core/package.json` を選ぶ。
3. `UnityProject/Assets/WorldSim` を作ったプロジェクトの `Assets/` にコピーする。
4. `Project Settings > Player > Active Input Handling` を **Input System Package** または **Both** にする。
5. 任意のシーンで再生すると `WorldSim` が自動生成される。
6. テスト：`Packages/manifest.json` に `"testables": ["com.worldsim.core"]` を追加し、Test Runner の EditMode で実行。

Unity版は表示・操作のコードのみで、Unity上での動作確認はまだ行っていない。

## 構成

- `Packages/com.worldsim.core/` — シミュレーション本体（純C#、UnityEngine参照禁止）
- `dotnet/` — .NET用プロジェクト（Core・Tests・CLI・Web）
- `UnityProject/Assets/WorldSim/` — Unity版の表示・入力・UI
- `docs/` — 仕様書・設計・進捗
