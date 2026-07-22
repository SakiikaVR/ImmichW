# Immich ランチャー (Windows ネイティブ / WinUI 3)

Docker / WSL を一切使わず、**Windows 上でネイティブに** [Immich](https://immich.app/) v3.0.3
を動かす構成と、その起動・停止を管理する **WinUI 3 製 GUI ランチャー**です。
ポート解放（ルーターのポートフォワード）は不要で、既定では `127.0.0.1` にのみバインドします。

> ⚠️ Immich のネイティブ実行は**公式にはサポートされていません**（公式は Docker のみ）。
> 本構成は実機で動作検証済み（起動・アカウント作成・アップロード・サムネイル生成まで確認）ですが、
> 本番運用の前に必ずバックアップ体制を整えてください。

## 構成

| コンポーネント | 実体 | 場所 |
|---|---|---|
| GUI ランチャー | WinUI 3 (C# / .NET 9) | `ImmichLauncher.WinUI\bin\x64\Release\...\ImmichLauncher.exe` |
| Immich サーバー | v3.0.3 をソースからビルド (Node.js) | `%LOCALAPPDATA%\ImmichNative\immich-src\server\dist` |
| Web UI | 同ソースの SvelteKit ビルド | `%LOCALAPPDATA%\ImmichNative\build\www` |
| PostgreSQL 17 | EDB ポータブル版 (サービス登録なし) | `%LOCALAPPDATA%\ImmichNative\pgsql` / データ: `pgdata` |
| pgvector 0.8.1 | MSVC でローカルビルド | `pgsql\lib\vector.dll` |
| Redis 8.8.0 | redis-windows (MSYS2 ネイティブビルド) | `%LOCALAPPDATA%\ImmichNative\redis` |
| ffmpeg 8.1.2 | gyan.dev essentials | `%LOCALAPPDATA%\ImmichNative\ffmpeg\bin` |
| 地理データ | GeoNames + Natural Earth (Docker 版と同一ソース) | `build\geodata` |
| 写真ライブラリ | — | `%USERPROFILE%\ImmichLibrary` |

すべて **管理者権限不要のポータブル配置**です。サービス登録はしていないため、
ランチャーの「起動」「停止」がそのままプロセスの起動・終了になります。
削除したい場合は `%LOCALAPPDATA%\ImmichNative` を消すだけです（写真は `ImmichLibrary` に別置き）。

## 使い方

1. `ImmichLauncher.exe` を起動
2. 「起動」→ PostgreSQL → Redis → Immich の順に立ち上がる（状態ランプが緑になる）
3. 「ブラウザで開く」→ `http://127.0.0.1:2283`
4. 管理者アカウントはランチャーの「設定」→「管理者アカウント」からいつでも変更できます
   （動作検証で `admin@example.com` を作成済みなので、自分のメール・パスワードに変更してください）。

### 管理者アカウントの設定（ランチャーから）

「設定」→「管理者アカウント」にメールアドレス・パスワード（・表示名）を入力して
「アカウントを設定」を押すと:
- 管理者が未作成なら **新規作成**
- 既存なら公式 CLI (`reset-admin-password`) で**パスワードをリセット**し、
  メールアドレスが違う場合は API 経由で**メールも変更**します

Immich 起動中に実行してください。

### 写真の保存先（ドライブ）変更

「設定」→「写真の保存先」で移動先（例: `D:\ImmichLibrary`）を指定して「ここへ移動」を押すと:
1. Immich を一時停止 → 2. `robocopy /MOVE` でファイル移動 → 3. 自動で再起動
（DB 内のパスは Immich サーバーが起動時に旧位置を検出して**自動移行**します。
途中で失敗した場合も、同じ移動先を指定してもう一度実行すれば続きから完了します）

### 外出先からのアクセス（ポート解放不要）

[Tailscale](https://tailscale.com/download) をインストールしてログイン後、
ランチャーの「外出先アクセスを有効化」を押すと `tailscale serve` により
`https://<PC名>.<tailnet>.ts.net` で **自分のアカウントのデバイスだけ**に HTTPS 公開されます。
インターネット全体には公開されません。スマホの Immich アプリにはこの URL を入力します
（スマホにも Tailscale アプリが必要）。

> **初回は tailnet 側の承認が必要です。** ボタンを押すと
> `https://login.tailscale.com/f/serve?node=...` の承認ページが自動でブラウザに開くので、
> そこで **Enable** を押してから、もう一度「外出先アクセスを有効化」を押してください
> （これが「つながらない」原因の正体で、Serve 機能が tailnet で未承認のままコマンドが
> 承認待ちでブロックしていました）。MagicDNS / HTTPS Certificates が無効な場合も
> 同様に案内が表示されます。

## セキュリティ設計

- Immich は `IMMICH_HOST=127.0.0.1` でループバックのみにバインド。PostgreSQL / Redis も同様
- DB パスワードは自動生成し `%LOCALAPPDATA%\ImmichNative\launcher-config.json` に保存
- 外部公開は Tailscale の暗号化トンネル経由のみ（ルーター設定不要・ポート解放なし）

## 機械学習（顔認識・スマート検索）について

ネイティブ構成では ML コンテナが無いため `IMMICH_MACHINE_LEARNING_ENABLED=false` で動かしています。
写真の閲覧・アップロード・アルバム・メタデータ検索・逆ジオコーディングは全て動作しますが、
顔認識と自然文検索（スマート検索）は無効です。必要になったら Python 3.11 +
`immich-src\machine-learning` を uv でセットアップすれば追加できます（未検証）。

## ソースに当てたパッチ（更新時に再適用が必要）

`server/src/dtos/env.dto.ts` の絶対パス判定が `/` 始まり（Linux 形式）のみだったため、
Windows ドライブパスを許可するよう 1 行変更しています:

```ts
// 変更前
const absolutePath = z.string().regex(/^\//, 'Must be an absolute path').optional();
// 変更後
const absolutePath = z
  .string()
  .regex(/^(?:\/|[A-Za-z]:[\\/])/, 'Must be an absolute path')
  .optional();
```

## Immich の更新手順（手動）

```powershell
cd $env:LOCALAPPDATA\ImmichNative\immich-src
git fetch --depth 1 origin tag <新タグ> ; git checkout <新タグ>
# ↑ の後、env.dto.ts のパッチを再適用
pnpm install --frozen-lockfile
pnpm --filter @immich/sdk build ; pnpm --filter @immich/plugin-sdk build
pnpm --filter immich build ; pnpm --filter immich-web build
Copy-Item web\build "$env:LOCALAPPDATA\ImmichNative\build\www" -Recurse -Force
```

DB マイグレーションはサーバー起動時に自動実行されます。**更新前に必ずバックアップ**
（`pg_dump` + `ImmichLibrary` のコピー）を取ってください。

## ライセンスについて（結論: 個人利用なら問題なし）

- **Windows 11 Home**: 個人用サーバーソフトの実行は EULA で禁止されていません。
  「20 デバイス制限」は Windows 自身の共有機能への接続に関する条項で、Immich には適用されません。
  商用ホスティング事業でなければ問題ありません。
- **Immich**: AGPL-3.0。セルフホストは自由。上記のローカルパッチも私的利用の範囲では公開義務なし
- **PostgreSQL / pgvector**: PostgreSQL License (BSD 系)。制限なし
- **Redis 8 (redis-windows ビルド)**: Redis 8 は AGPLv3 / RSALv2 / SSPLv1 のトリプルライセンス。
  自宅での個人利用はいずれでも問題なし（第三者へのマネージドサービス提供が制限対象）
- **ffmpeg**: LGPL/GPL ビルド。個人利用問題なし
- **Node.js / .NET / Windows App SDK**: MIT 等。問題なし
- **Tailscale**: 個人プラン無料（100 台まで）

※ Docker 版と違い、この構成は Docker Desktop のライセンス条件も WSL も関係ありません。

## 既知の注意点

- **Garnet は使えません**: Microsoft の Redis 互換サーバー Garnet は BullMQ の Lua スクリプトに
  未対応（`ERR Unknown Redis command called from script`）で起動に失敗します。実 Redis の
  Windows ビルド (redis-windows) が必要です（検証済み）。
- redis-windows はコミュニティビルドです。信頼性が気になる場合は自分で MSYS2 から
  ビルドすることもできます。
- PC 再起動後は自動起動しません。スタートアップに ImmichLauncher.exe を登録し「起動」を
  押すか、タスクスケジューラ登録を検討してください。

## 旧版: Rust + egui ランチャー (Docker 版)

リポジトリ直下の `Cargo.toml` / `src/main.rs` は最初に作った Docker/WSL2 ベースの
ランチャー（`target\release\immich-launcher.exe`）です。ネイティブ版に問題が出た場合の
フォールバックとして残しています。

## WinUI 3 ランチャーのビルド方法

```powershell
cd ImmichLauncher.WinUI
dotnet build -c Release -p:Platform=x64
```
