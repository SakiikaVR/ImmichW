using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichLauncher;

/// <summary>
/// ネイティブ Immich スタック (PostgreSQL / Redis / immich-server) の管理。
/// すべて %LOCALAPPDATA%\ImmichNative 配下のポータブル構成で、管理者権限不要。
/// Immich は 127.0.0.1 にのみバインドし、外部へは一切公開しない。
/// </summary>
public sealed class NativeStack
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmichNative");

    public static string PgDir => Path.Combine(Root, "pgsql");
    public static string PgDataDir => Path.Combine(Root, "pgdata");
    public static string RedisDir => Path.Combine(Root, "redis");
    public static string FfmpegBinDir => Path.Combine(Root, "ffmpeg", "bin");
    public static string ServerDir => Path.Combine(Root, "immich-src", "server");
    public static string BuildDataDir => Path.Combine(Root, "build");
    public static string LogDir => Path.Combine(Root, "logs");
    private static string ConfigPath => Path.Combine(Root, "launcher-config.json");

    public const string ImmichUrl = "http://127.0.0.1:2283";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private Process? _redis;
    private Process? _immich;

    public sealed record StackStatus(bool Postgres, bool Redis, bool Immich, bool Installed);

    // ---------- 状態 ----------

    public static async Task<StackStatus> ProbeAsync()
    {
        var installed = File.Exists(Path.Combine(PgDir, "bin", "pg_ctl.exe"))
                        && Directory.Exists(Path.Combine(ServerDir, "dist"));
        var pg = await PortOpenAsync(5432);
        var redis = await PortOpenAsync(6379);
        var immich = await PortOpenAsync(2283);
        return new StackStatus(pg, redis, immich, installed);
    }

    public static async Task<bool> PortOpenAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(400));
            return done == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 設定 ----------

    public sealed class LauncherConfig
    {
        public string DbPassword { get; set; } = "";
        public string? MediaLocation { get; set; }
    }

    public static string DefaultMediaLocation =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ImmichLibrary");

    public static string LibraryDir => LoadConfig().MediaLocation ?? DefaultMediaLocation;

    public static LauncherConfig LoadConfig()
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(ConfigPath))
        {
            try
            {
                return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath)) ?? NewConfig();
            }
            catch
            {
                // 壊れていれば作り直す
            }
        }
        return NewConfig();
    }

    public static void SaveConfig(LauncherConfig cfg) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg));

    private static LauncherConfig NewConfig()
    {
        var cfg = new LauncherConfig { DbPassword = RandomPassword() };
        SaveConfig(cfg);
        return cfg;
    }

    private static string RandomPassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(RandomNumberGenerator.GetItems<char>(chars, 24));
    }

    private static Dictionary<string, string> ImmichEnv(LauncherConfig cfg) => new()
    {
        ["NODE_ENV"] = "production",
        ["DB_HOSTNAME"] = "127.0.0.1",
        ["DB_PORT"] = "5432",
        ["DB_USERNAME"] = "immich",
        ["DB_PASSWORD"] = cfg.DbPassword,
        ["DB_DATABASE_NAME"] = "immich",
        ["DB_VECTOR_EXTENSION"] = "pgvector",
        ["REDIS_HOSTNAME"] = "127.0.0.1",
        ["REDIS_PORT"] = "6379",
        ["IMMICH_HOST"] = "127.0.0.1",
        ["IMMICH_PORT"] = "2283",
        ["IMMICH_MEDIA_LOCATION"] = cfg.MediaLocation ?? DefaultMediaLocation,
        ["IMMICH_BUILD_DATA"] = BuildDataDir,
        ["IMMICH_MACHINE_LEARNING_ENABLED"] = "false",
        ["PATH"] = FfmpegBinDir + ";" + Environment.GetEnvironmentVariable("PATH"),
    };

    // ---------- 起動 / 停止 ----------

    public async Task<bool> StartAllAsync(Action<string> log)
    {
        Directory.CreateDirectory(LogDir);
        var cfg = LoadConfig();
        Directory.CreateDirectory(cfg.MediaLocation ?? DefaultMediaLocation);

        // 1. PostgreSQL
        if (!await PortOpenAsync(5432))
        {
            log("PostgreSQL を起動しています…");
            RunTool(Path.Combine(PgDir, "bin", "pg_ctl.exe"),
                $"-D \"{PgDataDir}\" -l \"{Path.Combine(LogDir, "postgres.log")}\" -o \"-h 127.0.0.1\" start");
            if (!await WaitPortAsync(5432, 30)) { log("PostgreSQL の起動に失敗しました。logs\\postgres.log を確認してください。"); return false; }
        }
        log("PostgreSQL: 起動済み (127.0.0.1:5432)");

        // 2. Redis (redis-windows ネイティブビルド)
        if (!await PortOpenAsync(6379))
        {
            log("Redis を起動しています…");
            _redis = StartDaemon(
                Path.Combine(RedisDir, "redis-server.exe"),
                "--bind 127.0.0.1 --port 6379 --save \"\" --appendonly no",
                RedisDir, Path.Combine(LogDir, "redis.log"));
            if (!await WaitPortAsync(6379, 20)) { log("Redis の起動に失敗しました。logs\\redis.log を確認してください。"); return false; }
        }
        log("Redis: 起動済み (127.0.0.1:6379)");

        // 3. immich-server
        if (!await PortOpenAsync(2283))
        {
            log("Immich サーバーを起動しています(初回はマイグレーションに時間がかかります)…");
            _immich = StartDaemon("node", "dist\\main.js", ServerDir,
                Path.Combine(LogDir, "immich.log"), ImmichEnv(cfg));
            if (!await WaitPortAsync(2283, 120)) { log("Immich の起動に失敗しました。logs\\immich.log を確認してください。"); return false; }
        }
        log($"Immich: 起動済み {ImmichUrl}");
        return true;
    }

    public async Task StopAllAsync(Action<string> log)
    {
        await StopImmichProcessAsync(log);

        log("Redis を停止しています…");
        KillTracked(_redis);
        _redis = null;
        KillByCommandLine("redis-server.exe", "redis-server");

        log("PostgreSQL を停止しています…");
        RunTool(Path.Combine(PgDir, "bin", "pg_ctl.exe"), $"-D \"{PgDataDir}\" -m fast stop");
        await Task.Delay(1000);
        log("停止処理が完了しました。");
    }

    private async Task<bool> StopImmichProcessAsync(Action<string> log)
    {
        log("Immich を停止しています…");
        KillTracked(_immich);
        _immich = null;
        // パス区切りはどちらもあり得る (シェル起動なら / になる)
        KillByCommandLine("node.exe", "dist\\main.js");
        KillByCommandLine("node.exe", "dist/main.js");
        for (var i = 0; i < 15; i++)
        {
            if (!await PortOpenAsync(2283)) return true;
            await Task.Delay(1000);
        }
        log("Immich プロセスを停止できませんでした。タスクマネージャーで node.exe を終了してから再実行してください。");
        return false;
    }

    // ---------- 管理者アカウント設定 ----------

    /// <summary>
    /// 管理者のメール・パスワード(・表示名)を設定する。
    /// 管理者が未作成なら API で新規作成、既存ならパスワードを CLI でリセットし、
    /// メールが異なる場合は API でメールも変更する。Immich 起動中であること。
    /// </summary>
    public async Task<bool> SetAdminAccountAsync(string email, string password, string name, Action<string> log)
    {
        if (!await PortOpenAsync(2283))
        {
            log("Immich が起動していません。先に「起動」してください。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            log("メールアドレスとパスワードを入力してください。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(name)) name = "Admin";

        // 1) 管理者未作成なら新規作成
        var signUp = await Http.PostAsJsonAsync($"{ImmichUrl}/api/auth/admin-sign-up",
            new { email, password, name });
        if (signUp.IsSuccessStatusCode)
        {
            log($"管理者アカウントを新規作成しました: {email}");
            return true;
        }

        // 2) 既存 → CLI でパスワードをリセット(現在の管理者メール/ID も出力から取得できる)
        log("既存の管理者のパスワードを更新しています…");
        var output = await RunCliAsync("reset-admin-password", password + "\n", log);
        var idMatch = Regex.Match(output, @"ID=([0-9a-f-]{36})");
        var emailMatch = Regex.Match(output, @"Email=(\S+)");
        if (!output.Contains("has been updated") || !idMatch.Success || !emailMatch.Success)
        {
            log("パスワードのリセットに失敗しました。ログを確認してください。");
            return false;
        }
        var adminId = idMatch.Groups[1].Value;
        var currentEmail = emailMatch.Groups[1].Value;
        log("パスワードを更新しました。");

        // 3) メール変更が必要なら API で変更
        if (!string.Equals(currentEmail, email, StringComparison.OrdinalIgnoreCase))
        {
            log($"メールアドレスを {currentEmail} → {email} に変更しています…");
            var login = await Http.PostAsJsonAsync($"{ImmichUrl}/api/auth/login",
                new { email = currentEmail, password });
            if (!login.IsSuccessStatusCode)
            {
                log("新パスワードでのログインに失敗したため、メール変更を中断しました。");
                return false;
            }
            var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ImmichUrl}/api/admin/users/{adminId}")
            {
                Content = JsonContent.Create(new { email, name }),
            };
            req.Headers.Authorization = new("Bearer", token);
            var update = await Http.SendAsync(req);
            if (!update.IsSuccessStatusCode)
            {
                log($"メール変更に失敗しました: HTTP {(int)update.StatusCode} {await update.Content.ReadAsStringAsync()}");
                return false;
            }
        }
        log($"管理者アカウントを設定しました: {email}");
        return true;
    }

    // ---------- 保存先 (ライブラリ) の移動 ----------

    /// <summary>
    /// 写真ライブラリを別のフォルダ/ドライブへ移動する。
    /// ファイルを robocopy で移動 → 公式 CLI change-media-location で DB 内パスを書き換え →
    /// 設定を保存して Immich を再起動する。
    /// </summary>
    public async Task<bool> ChangeMediaLocationAsync(string newLocation, Action<string> log)
    {
        var cfg = LoadConfig();
        var oldLocation = cfg.MediaLocation ?? DefaultMediaLocation;
        newLocation = Path.GetFullPath(newLocation.Trim());

        if (string.Equals(oldLocation, newLocation, StringComparison.OrdinalIgnoreCase))
        {
            log("移動先が現在の保存先と同じです。");
            return false;
        }
        if (!Path.IsPathFullyQualified(newLocation))
        {
            log("移動先は絶対パス (例: D:\\ImmichLibrary) で指定してください。");
            return false;
        }
        if ((newLocation + Path.DirectorySeparatorChar)
            .StartsWith(oldLocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            log("移動先を現在の保存先の中にすることはできません。");
            return false;
        }
        var drive = Path.GetPathRoot(newLocation);
        if (drive == null || !Directory.Exists(drive))
        {
            log($"ドライブ {drive} が見つかりません。");
            return false;
        }

        // 1) Immich のみ停止し、確実に止まったことを確認してから移動する
        //    (稼働中に移動するとロック中のファイルが取り残され、新旧の場所に分裂する)
        if (!await StopImmichProcessAsync(log)) return false;
        if (!await PortOpenAsync(5432))
        {
            log("PostgreSQL を起動しています…");
            RunTool(Path.Combine(PgDir, "bin", "pg_ctl.exe"),
                $"-D \"{PgDataDir}\" -l \"{Path.Combine(LogDir, "postgres.log")}\" -o \"-h 127.0.0.1\" start");
            if (!await WaitPortAsync(5432, 30)) { log("PostgreSQL を起動できません。"); return false; }
        }

        // 2) ファイル移動
        if (Directory.Exists(oldLocation))
        {
            log($"ファイルを移動しています… {oldLocation} → {newLocation}(サイズによっては時間がかかります)");
            Directory.CreateDirectory(newLocation);
            var rc = await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo("robocopy.exe",
                    $"\"{oldLocation}\" \"{newLocation}\" /E /MOVE /NFL /NDL /NJH /NP /R:2 /W:2")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                })!;
                p.WaitForExit();
                return p.ExitCode;
            });
            if (rc >= 8)
            {
                log($"ファイル移動に失敗しました (robocopy exit {rc})。元のフォルダは保持されています。");
                return false;
            }
            log("ファイル移動が完了しました。");
        }
        else
        {
            Directory.CreateDirectory(newLocation);
            log("既存ライブラリが無いため、新しいフォルダを作成しました。");
        }

        // 3) 設定保存 → 再起動。DB 内のパス書き換えは Immich サーバーが起動時に
        //    自動移行する (storage.service の media-location 検出) ため、ここでは行わない
        cfg.MediaLocation = newLocation;
        SaveConfig(cfg);
        log($"保存先を {newLocation} に変更しました。Immich を再起動します(起動時に DB 内パスが自動移行されます)…");
        return await StartAllAsync(log);
    }

    /// <summary>
    /// (現在未使用・保守用) DB 内パスの直接書き換え。通常はサーバー起動時の自動移行に任せる。
    /// </summary>
    internal static async Task<bool> MigrateDbPathsAsync(string oldLocation, string newLocation, Action<string> log)
    {
        // 正規表現の特殊文字をエスケープ (公式実装と同じ文字集合)
        var pattern = "^" + Regex.Replace(oldLocation, @"[-\[\]{}()*+?.,\\^$|#\s]", @"\$&");
        // REGEXP_REPLACE の置換文字列ではバックスラッシュを二重化する必要がある
        var replacement = newLocation.Replace("\\", "\\\\");
        string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        var sql = new StringBuilder();
        sql.AppendLine("BEGIN;");
        foreach (var (table, column) in new[]
                 {
                     ("asset", "originalPath"),
                     ("asset_file", "path"),
                     ("person", "thumbnailPath"),
                     ("user", "profileImagePath"),
                 })
        {
            sql.AppendLine(
                $"UPDATE \"{table}\" SET \"{column}\" = REGEXP_REPLACE(\"{column}\", {Quote(pattern)}, {Quote(replacement)});");
        }
        sql.AppendLine("COMMIT;");

        var sqlFile = Path.Combine(LogDir, "migrate-paths.sql");
        Directory.CreateDirectory(LogDir);
        await File.WriteAllTextAsync(sqlFile, sql.ToString(), new UTF8Encoding(false));

        var cfg = LoadConfig();
        var psi = new ProcessStartInfo(Path.Combine(PgDir, "bin", "psql.exe"),
            $"-v ON_ERROR_STOP=1 -h 127.0.0.1 -U immich -d immich -f \"{sqlFile}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PGPASSWORD"] = cfg.DbPassword;
        psi.Environment["PGCLIENTENCODING"] = "UTF8";
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        foreach (var line in (stdout + stderr).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) log("  " + t);
        }
        return p.ExitCode == 0;
    }

    // ---------- 初回自動セットアップ ----------

    private const string PgZipUrl = "https://sbp.enterprisedb.com/getfile.jsp?fileid=1260307"; // PostgreSQL 17.10 x64 binaries
    private const string RedisZipUrl =
        "https://github.com/redis-windows/redis-windows/releases/download/8.8.0/Redis-8.8.0-Windows-x64-msys2.zip";
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string ImmichSrcZipUrl = "https://github.com/immich-app/immich/archive/refs/tags/v3.0.3.zip";
    private const string ImmichSrcFolderName = "immich-3.0.3";

    private static string NodeDir => @"C:\Program Files\nodejs";
    private static string NpmGlobalDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
    private static string SrcDir => Path.Combine(Root, "immich-src");

    /// <summary>
    /// 初回インストールをすべて自動で行う。何度でも再実行可能(完了済みの手順はスキップ)。
    /// </summary>
    public async Task<bool> SetupStackAsync(Action<string> log)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
        var cfg = LoadConfig();

        try
        {
            // 1. Node.js
            if (!File.Exists(Path.Combine(NodeDir, "node.exe")) && !await ToolOkAsync("node --version"))
            {
                log("[1/9] Node.js をインストールしています (UAC ダイアログが出たら許可してください)…");
                await RunLoggedAsync("winget",
                    "install -e --id OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements",
                    null, log);
                if (!File.Exists(Path.Combine(NodeDir, "node.exe")))
                {
                    log("Node.js のインストールを確認できませんでした。https://nodejs.org/ から LTS 版を入れて再実行してください。");
                    return false;
                }
            }
            log("[1/9] Node.js: OK");

            // 2. PostgreSQL (ポータブル)
            if (!File.Exists(Path.Combine(PgDir, "bin", "pg_ctl.exe")))
            {
                log("[2/9] PostgreSQL 17 をダウンロードしています (約320MB)…");
                var zip = Path.Combine(Root, "pg17.zip");
                await DownloadAsync(PgZipUrl, zip, log);
                log("展開中…");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, Root, overwriteFiles: true);
                File.Delete(zip);
            }
            log("[2/9] PostgreSQL: OK");

            // 3. pgvector (同梱のビルド済みバイナリを配置)
            var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "pgvector");
            if (Directory.Exists(bundled))
            {
                CopyTree(Path.Combine(bundled, "lib"), Path.Combine(PgDir, "lib"));
                CopyTree(Path.Combine(bundled, "share"), Path.Combine(PgDir, "share"));
                log("[3/9] pgvector: OK (同梱バイナリを配置)");
            }
            else if (!File.Exists(Path.Combine(PgDir, "lib", "vector.dll")))
            {
                log("[3/9] pgvector の同梱ファイルが見つかりません。配布 ZIP を丸ごと展開して実行してください。");
                return false;
            }

            // 4. Redis
            if (!File.Exists(Path.Combine(RedisDir, "redis-server.exe")))
            {
                log("[4/9] Redis (redis-windows) をダウンロードしています (約40MB)…");
                var zip = Path.Combine(Root, "redis.zip");
                await DownloadAsync(RedisZipUrl, zip, log);
                var tmp = Path.Combine(Root, "redis-tmp");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp, overwriteFiles: true);
                var inner = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;
                Directory.Move(inner, RedisDir);
                try { Directory.Delete(tmp, recursive: true); } catch { /* 空でなければ残す */ }
                File.Delete(zip);
            }
            log("[4/9] Redis: OK");

            // 5. ffmpeg
            if (!File.Exists(Path.Combine(FfmpegBinDir, "ffmpeg.exe")))
            {
                log("[5/9] ffmpeg をダウンロードしています (約100MB)…");
                var zip = Path.Combine(Root, "ffmpeg.zip");
                await DownloadAsync(FfmpegZipUrl, zip, log);
                var tmp = Path.Combine(Root, "ffmpeg-tmp");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp, overwriteFiles: true);
                var inner = Directory.GetDirectories(tmp).FirstOrDefault();
                if (inner != null)
                {
                    Directory.CreateDirectory(Path.Combine(Root, "ffmpeg"));
                    if (Directory.Exists(FfmpegBinDir)) Directory.Delete(FfmpegBinDir, recursive: true);
                    Directory.Move(Path.Combine(inner, "bin"), FfmpegBinDir);
                }
                try { Directory.Delete(tmp, recursive: true); } catch { /* 残っても害なし */ }
                File.Delete(zip);
            }
            log("[5/9] ffmpeg: OK");

            // 6. Immich ソース取得 + Windows パッチ
            if (!File.Exists(Path.Combine(SrcDir, "package.json")))
            {
                log("[6/9] Immich v3.0.3 のソースをダウンロードしています…");
                var zip = Path.Combine(Root, "immich-src.zip");
                await DownloadAsync(ImmichSrcZipUrl, zip, log);
                log("展開中 (数分かかります)…");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, Root, overwriteFiles: true);
                Directory.Move(Path.Combine(Root, ImmichSrcFolderName), SrcDir);
                File.Delete(zip);
            }
            ApplyWindowsPatch(log);
            log("[6/9] Immich ソース: OK");

            // 7. ビルド (初回は 10〜20 分かかります)
            if (!File.Exists(Path.Combine(ServerDir, "dist", "main.js")))
            {
                log("[7/9] 依存関係をインストールしています (数分)…");
                if (!await RunLoggedAsync("cmd.exe", "/c npm i -g pnpm@11.6.0", null, log)) return false;
                if (!await RunLoggedAsync("cmd.exe", "/c pnpm install --frozen-lockfile", SrcDir, log,
                        quiet: true)) return false;
                log("[7/9] サーバーと Web UI をビルドしています (10〜20分)…");
                foreach (var filter in new[] { "@immich/sdk", "@immich/plugin-sdk", "immich", "immich-web" })
                {
                    log($"  build: {filter}");
                    if (!await RunLoggedAsync("cmd.exe", $"/c pnpm --filter {filter} build", SrcDir, log,
                            quiet: true))
                    {
                        return false;
                    }
                }
            }
            var www = Path.Combine(BuildDataDir, "www");
            if (!File.Exists(Path.Combine(www, "index.html")))
            {
                CopyTree(Path.Combine(SrcDir, "web", "build"), www);
            }
            log("[7/9] ビルド: OK");

            // 8. 地理データ (逆ジオコーディング用)
            var geo = Path.Combine(BuildDataDir, "geodata");
            if (!File.Exists(Path.Combine(geo, "cities500.txt")))
            {
                log("[8/9] 地理データをダウンロードしています…");
                Directory.CreateDirectory(geo);
                var cityZip = Path.Combine(geo, "cities500.zip");
                await DownloadAsync("https://download.geonames.org/export/dump/cities500.zip", cityZip, log);
                System.IO.Compression.ZipFile.ExtractToDirectory(cityZip, geo, overwriteFiles: true);
                File.Delete(cityZip);
                await DownloadAsync("https://download.geonames.org/export/dump/admin1CodesASCII.txt",
                    Path.Combine(geo, "admin1CodesASCII.txt"), log);
                await DownloadAsync("https://download.geonames.org/export/dump/admin2Codes.txt",
                    Path.Combine(geo, "admin2Codes.txt"), log);
                await DownloadAsync(
                    "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_10m_admin_0_countries.geojson",
                    Path.Combine(geo, "ne_10m_admin_0_countries.geojson"), log);
                await File.WriteAllTextAsync(Path.Combine(geo, "geodata-date.txt"),
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00"));
            }
            log("[8/9] 地理データ: OK");

            // 9. データベース初期化
            if (!Directory.Exists(PgDataDir))
            {
                log("[9/9] データベースを初期化しています…");
                var pwFile = Path.Combine(Root, "pgpw.tmp");
                await File.WriteAllTextAsync(pwFile, cfg.DbPassword, new UTF8Encoding(false));
                var ok = await RunLoggedAsync(Path.Combine(PgDir, "bin", "initdb.exe"),
                    $"-D \"{PgDataDir}\" -U postgres -A scram-sha-256 --pwfile=\"{pwFile}\" -E UTF8 --locale=C",
                    null, log, quiet: true);
                File.Delete(pwFile);
                if (!ok) return false;

                RunTool(Path.Combine(PgDir, "bin", "pg_ctl.exe"),
                    $"-D \"{PgDataDir}\" -l \"{Path.Combine(LogDir, "postgres.log")}\" -o \"-h 127.0.0.1\" start");
                if (!await WaitPortAsync(5432, 30)) { log("PostgreSQL を起動できませんでした。"); return false; }

                var psql = Path.Combine(PgDir, "bin", "psql.exe");
                var env = new Dictionary<string, string>
                {
                    ["PGPASSWORD"] = cfg.DbPassword,
                    ["PGCLIENTENCODING"] = "UTF8",
                };
                await RunLoggedAsync(psql,
                    $"-h 127.0.0.1 -U postgres -c \"CREATE ROLE immich LOGIN SUPERUSER PASSWORD '{cfg.DbPassword}'\"",
                    null, log, env);
                await RunLoggedAsync(psql, "-h 127.0.0.1 -U postgres -c \"CREATE DATABASE immich OWNER immich\"",
                    null, log, env);
                await RunLoggedAsync(psql,
                    "-h 127.0.0.1 -U immich -d immich -c \"CREATE EXTENSION IF NOT EXISTS vector\"",
                    null, log, env);
            }
            log("[9/9] データベース: OK");

            log("🎉 セットアップが完了しました。「起動」を押してください。");
            return true;
        }
        catch (Exception ex)
        {
            log($"セットアップ中にエラーが発生しました: {ex.Message}");
            log("もう一度「自動セットアップ」を押すと、完了済みの手順をスキップして続きから再開します。");
            return false;
        }
    }

    private static void ApplyWindowsPatch(Action<string> log)
    {
        // Windows のドライブパスを絶対パスとして許可する 1 行パッチ
        var dto = Path.Combine(SrcDir, "server", "src", "dtos", "env.dto.ts");
        if (!File.Exists(dto)) return;
        var text = File.ReadAllText(dto);
        const string original = @"z.string().regex(/^\//, 'Must be an absolute path')";
        const string patched = @"z.string().regex(/^(?:\/|[A-Za-z]:[\\/])/, 'Must be an absolute path')";
        if (text.Contains(original))
        {
            File.WriteAllText(dto, text.Replace(original, patched));
            log("Windows 用パッチを適用しました (env.dto.ts)");
        }
    }

    private static async Task<bool> ToolOkAsync(string commandLine)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + commandLine)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DownloadAsync(string url, string dest, Action<string> log)
    {
        using var response = await Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var src = await response.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buffer = new byte[1 << 20];
        long done = 0;
        var lastPct = -25;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0)
            {
                var pct = (int)(done * 100 / total);
                if (pct >= lastPct + 25)
                {
                    lastPct = pct;
                    log($"  {pct}% ({done / 1024 / 1024}MB / {total / 1024 / 1024}MB)");
                }
            }
        }
    }

    private static void CopyTree(string source, string dest)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }

    /// コマンドを実行し出力をログへ流す。quiet: true なら末尾のエラー行のみログに出す
    private static async Task<bool> RunLoggedAsync(string file, string args, string? cwd, Action<string> log,
        Dictionary<string, string>? env = null, bool quiet = false)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        psi.Environment["PATH"] = NodeDir + ";" + NpmGlobalDir + ";" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["CI"] = "1";
        if (env != null)
        {
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (line == null) return;
            if (quiet)
            {
                tail.Enqueue(line);
                if (tail.Count > 15) tail.Dequeue();
            }
            else if (line.Trim().Length > 0)
            {
                log("  " + line);
            }
        }
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            log($"  [失敗: {Path.GetFileName(file)} {args} (exit {p.ExitCode})]");
            foreach (var line in tail) log("  " + line);
            return false;
        }
        return true;
    }

    // ---------- immich-admin CLI ----------

    private static async Task<string> RunCliAsync(string command, string stdinText, Action<string> log)
    {
        var cfg = LoadConfig();
        var psi = new ProcessStartInfo("node", $"dist\\main.js immich-admin {command}")
        {
            WorkingDirectory = ServerDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (k, v) in ImmichEnv(cfg)) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(stdinText);
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 終了済みなら無視 */ }
            log($"CLI {command} がタイムアウトしました。");
        }
        var output = await stdoutTask + await stderrTask;
        // 対話プロンプトの制御文字を除去してからログへ要約を出す
        var clean = Regex.Replace(output, @"\x1b\[[0-9;]*[A-Za-z]|\[\d+[A-Z]", "");
        foreach (var line in clean.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("[Nest]") && !t.Contains("ExperimentalWarning") && !t.StartsWith("?"))
            {
                log("  " + t);
            }
        }
        return clean;
    }

    // ---------- 内部ヘルパー ----------

    private static async Task<bool> WaitPortAsync(int port, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            if (await PortOpenAsync(port)) return true;
            await Task.Delay(1000);
        }
        return false;
    }

    private static void RunTool(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        p?.WaitForExit(20_000);
    }

    private static Process StartDaemon(string file, string args, string cwd, string logFile,
        Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (env != null)
        {
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }
        var p = Process.Start(psi)!;
        var writer = TextWriter.Synchronized(new StreamWriter(logFile, append: true) { AutoFlush = true });
        p.OutputDataReceived += (_, e) => { if (e.Data != null) writer.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) writer.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static void KillTracked(Process? p)
    {
        try
        {
            if (p is { HasExited: false }) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 既に終了している場合は無視
        }
    }

    /// ランチャー再起動後など、追跡できていない常駐プロセスをコマンドラインで特定して終了する
    private static void KillByCommandLine(string exeName, string commandLineFragment)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{exeName}'");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                var cmd = mo["CommandLine"] as string ?? "";
                if (!cmd.Contains(commandLineFragment, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(entireProcessTree: true);
                }
                catch
                {
                    // 対象が既に終了していれば無視
                }
            }
        }
        catch
        {
            // WMI が使えない環境では追跡済みプロセスの停止のみ行う
        }
    }

    // ---------- Tailscale ----------

    public static string? TailscaleExe()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Tailscale\tailscale.exe",
            "tailscale",
        };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(c, "version")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                p!.WaitForExit(5000);
                if (p.ExitCode == 0) return c;
            }
            catch
            {
                // 次の候補へ
            }
        }
        return null;
    }

    /// <summary>
    /// tailscale コマンドを実行して出力を返す。tailnet 側で Serve が未承認の場合、
    /// コマンドが承認待ちでブロックするため、タイムアウトで打ち切って
    /// 出力に含まれる承認 URL を返す (呼び出し側でブラウザを開く)。
    /// </summary>
    public static async Task<string> RunCaptureAsync(string file, string args, int timeoutSeconds = 20)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 終了済みなら無視 */ }
        }
        return ((await stdoutTask) + (await stderrTask)).Trim();
    }

    /// Tailscale の出力から承認 URL (https://login.tailscale.com/f/...) を取り出す
    public static string? ExtractApprovalUrl(string output)
    {
        var m = Regex.Match(output, @"https://login\.tailscale\.com/\S+");
        return m.Success ? m.Value : null;
    }
}
