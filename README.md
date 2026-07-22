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
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-AGPL--3.0-blue?style=for-the-badge" alt="License" />
  </a>
</p>

---

## ⬇️ ダウンロード

**[📦 最新リリースはこちら (Releases)](https://github.com/SakiikaVR/ImmichW/releases/latest)** — `ImmichW-win-x64.zip` を解凍して `ImmichLauncher.exe` を実行してください。

> **初回インストールはボタン一つ・ビルド不要。** 起動すると表示される「⬇ 自動セットアップを実行」を
> 押すだけで、必要なもの一式 (PostgreSQL 17 + pgvector / Redis 8 / ffmpeg / Node.js /
> **ビルド済み Immich v3.0.3** / 地理データ) を 1 つのバンドル (約400MB) としてダウンロードし、
> `%LOCALAPPDATA%\ImmichNative` に管理者権限不要のポータブル構成で展開します。数分で完了します。
> 完了後は起動・停止・アカウント設定・保存先ドライブ変更・外出先アクセス (Tailscale) まで
> すべて GUI で操作できます。同梱物のライセンスはバンドル内の THIRD-PARTY-NOTICES.md と
> 下記「ライセンスと表記」を、手動での構築手順は [docs/SETUP.md](docs/SETUP.md) を参照。

## ✨ 特徴

- 🚫 **Docker / WSL 不要** — Windows ネイティブで動作
- 🔒 **ポート解放不要** — `127.0.0.1` のみにバインド。外部公開は Tailscale の暗号化トンネル経由のみ
- 🖥️ **WinUI 3 の GUI** — 起動 / 停止 / 状態表示 / 管理者アカウント設定 / 写真保存先のドライブ変更
- 🌍 **外出先アクセス** — `tailscale serve` によるあなたのデバイス限定の HTTPS 公開(ボタン一つ)

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

## 🛠️ ビルド方法

```powershell
cd ImmichLauncher.WinUI
dotnet build -c Release -p:Platform=x64
```
