<p align="center">
  <img src="assets/logo.png" width="120" alt="ImmichW logo" />
</p>

<h1 align="center">ImmichW</h1>

<p align="center"><b>Immich フォト Windows クライアント【Un Official / 非公式】</b></p>

<p align="center">
  Docker / WSL 不要・ポート解放不要で、Windows 上にネイティブに
  <a href="https://immich.app/">Immich</a>(セルフホスト写真管理) を建てて管理する WinUI 3 製ランチャーです。
</p>

<p align="center">
  <a href="https://github.com/SakiikaVR/ImmichW/releases/latest">
    <img src="https://img.shields.io/github/v/release/SakiikaVR/ImmichW?style=for-the-badge&label=%E2%AC%87%20%E3%83%80%E3%82%A6%E3%83%B3%E3%83%AD%E3%83%BC%E3%83%89&color=4250af" alt="Download" />
  </a>
  <a href="https://github.com/SakiikaVR/ImmichW/releases/latest">
    <img src="https://img.shields.io/github/downloads/SakiikaVR/ImmichW/total?style=for-the-badge&label=DL%E6%95%B0&color=10a05a" alt="Downloads" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-AGPL--3.0-blue?style=for-the-badge" alt="License" />
  </a>
</p>

---

## 🚀 クイックスタート (3 ステップ)

1. **[📦 最新リリース](https://github.com/SakiikaVR/ImmichW/releases/latest)** から `ImmichW-win-x64.zip` をダウンロードして解凍し、`ImmichLauncher.exe` を実行
2. 画面の「**⬇ 自動セットアップを実行**」を押す(初回のみ。必要なもの一式・約350MB を自動ダウンロードして展開。**ビルド不要・数分で完了**)
3. 「**起動**」→「**ブラウザで開く**」→ 管理者アカウントを作成して完了 🎉

> インストール先はすべて `%LOCALAPPDATA%\ImmichNative`(管理者権限不要のポータブル構成)。
> 写真の保存先は既定で `%USERPROFILE%\ImmichLibrary`(後から GUI でドライブごと変更可能)。

## ✨ 機能

- 🚫 **Docker / WSL 不要** — PostgreSQL・Redis・Immich をすべて Windows ネイティブのプロセスとして実行
- 🔒 **ポート解放不要** — `127.0.0.1` のみにバインド。LAN にもインターネットにも公開されません
- 📦 **ワンボタン・セットアップ** — ビルド済み一式(PostgreSQL 17 + pgvector / Redis 8 / ffmpeg / Node.js / Immich v3.0.3 / 地理データ)を自動配置。破損を検出すると自動修復します
- 🖥️ **WinUI 3 の GUI** — 起動 / 停止 / 状態ランプ / ログ表示
- 👤 **管理者アカウント設定** — メール・パスワードの新規作成もリセットも GUI から
- 💾 **保存先ドライブ変更** — 写真ライブラリを D: など別ドライブへ GUI で移動(ファイル移動と DB 移行を自動処理)
- 🌍 **外出先アクセス** — Tailscale の暗号化トンネルで、**自分のデバイスだけ**に HTTPS 公開(`tailscale serve`)。ボタン一つで有効化/無効化

## 📱 スマホには公式 Immich アプリを

このプロジェクトはサーバー側のランチャーです。スマホからは**公式アプリ**で接続します:

| | Immich (公式) |
|---|---|
| Android | <a href="https://play.google.com/store/apps/details?id=app.alextran.immich"><img src="https://play.google.com/intl/ja/badges/static/images/badges/ja_badge_web_generic.png" height="60" alt="Google Play で手に入れよう" /></a> |
| iPhone / iPad | <a href="https://apps.apple.com/jp/app/immich/id1613945652"><img src="https://tools.applemediaservices.com/api/badges/download-on-the-app-store/black/ja-jp" height="41" alt="App Store からダウンロード" /></a> |

サーバー URL には、ランチャーで有効化した Tailscale の `https://<PC名>.<tailnet>.ts.net` を入力します。

## 🔗 Tailscale (外出先アクセスに必要)

PC とスマホの両方に入れて、**同じアカウント**でログインしてください。

| プラットフォーム | リンク |
|---|---|
| Windows (このPC) | [tailscale.com/download/windows](https://tailscale.com/download/windows) |
| Android | <a href="https://play.google.com/store/apps/details?id=com.tailscale.ipn"><img src="https://play.google.com/intl/ja/badges/static/images/badges/ja_badge_web_generic.png" height="60" alt="Google Play で手に入れよう" /></a> |
| iPhone / iPad | <a href="https://apps.apple.com/jp/app/tailscale/id1470499037"><img src="https://tools.applemediaservices.com/api/badges/download-on-the-app-store/black/ja-jp" height="41" alt="App Store からダウンロード" /></a> |

## ❓ トラブルシューティング

| 症状 | 対処 |
|---|---|
| `Cannot find module 'kysely'` で起動しない (v0.3.0) | v0.3.1 以降のランチャーに差し替えて「自動セットアップ」を再実行(破損を自動修復します) |
| 「外出先アクセスを有効化」で承認ページが開く | Tailscale の仕様で初回は tailnet の Serve 機能承認が必要です。開いたページで **Enable HTTPS** を押し(**Funnel のチェックは外す**)、もう一度ボタンを押してください |
| スマホから `Server is not reachable` | スマホに Tailscale アプリを入れ、PC と**同じアカウント**でログインして VPN を ON にしてください |
| ストレージ使用量が表示されない | 「停止」→「起動」で再起動してください。保存先変更の直後に起きた場合は自動修復されます |
| うまくいかない時 | 「ログフォルダ」の `immich.log` / `postgres.log` / `redis.log` を確認 |

## 🗑️ アンインストール

1. ランチャーで「停止」
2. `%LOCALAPPDATA%\ImmichNative` フォルダを削除(写真は含まれません)
3. 写真を消す場合のみライブラリフォルダ(既定 `%USERPROFILE%\ImmichLibrary`)を削除
4. ランチャーの解凍フォルダを削除

レジストリやサービスには何も登録しないため、これだけで完全に消えます。

## 💻 動作環境

- Windows 11 x64 (Windows 10 21H2 以降でも動作想定)
- 空きディスク約 2GB + 写真データ分
- .NET ランタイム同梱のためインストール不要

## 🔗 公式プロジェクト

- Immich 公式サイト: <https://immich.app/>
- Immich GitHub: <https://github.com/immich-app/immich>
- Immich ドキュメント: <https://docs.immich.app/>

## 📄 ライセンスと表記

- 本リポジトリは **AGPL-3.0** で公開しています ([LICENSE](LICENSE))。
- **本プロジェクトは非公式 (Un Official) です。** Immich プロジェクトおよび FUTO とは無関係で、
  承認・提携を受けていません。
- 「Immich」の名称・ロゴは Immich プロジェクト ([immich-app/immich](https://github.com/immich-app/immich),
  AGPL-3.0) に由来します。ロゴ画像は同プロジェクトの公開アセットを使用しています。
- スタックバンドル (`ImmichW-stack-win-x64.zip`) には **AGPL-3.0 に基づき Immich v3.0.3 の
  ビルド済みサーバーを同梱**しています。対応ソースコードは
  [immich-app/immich v3.0.3](https://github.com/immich-app/immich/tree/v3.0.3) と、適用済みの
  1 行パッチ ([docs/SETUP.md](docs/SETUP.md) に記載) から入手できます。
- その他の同梱物: PostgreSQL 17 + pgvector (PostgreSQL License) /
  Redis 8 ([redis-windows](https://github.com/redis-windows/redis-windows) ビルド, AGPLv3 を選択して再配布) /
  ffmpeg ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/) GPL ビルド) / Node.js (MIT) /
  GeoNames 地理データ (CC BY 4.0) / Natural Earth (パブリックドメイン) / Windows App SDK (MIT)。
  詳細はバンドル内 THIRD-PARTY-NOTICES.md を参照してください。

## 🛠️ 開発者向け

ランチャーのビルド:

```powershell
cd ImmichLauncher.WinUI
dotnet build -c Release -p:Platform=x64
```

Immich サーバーを自分でソースからビルドする手順(バンドルを使わない場合)や、
スタックの内部構成・適用パッチの詳細は [docs/SETUP.md](docs/SETUP.md) を参照してください。
