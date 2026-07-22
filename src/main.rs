// Immich Launcher — Immich を Windows 上でポート解放なしに安全に建てる GUI ランチャー
// - Immich は 127.0.0.1 にのみバインド(既定)。LAN・インターネットへは一切公開しない
// - 外出先からは Tailscale + `tailscale serve` (自分のデバイスだけに HTTPS 公開) を使う
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::{BufReader, Read};
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use eframe::egui;
use rand::{distributions::Alphanumeric, Rng};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const IMMICH_URL: &str = "http://127.0.0.1:2283";
const DOCKER_DESKTOP_EXE: &str = r"C:\Program Files\Docker\Docker\Docker Desktop.exe";
const DOCKER_CLI_FALLBACK: &str = r"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
const TAILSCALE_FALLBACK: &str = r"C:\Program Files\Tailscale\tailscale.exe";
const COMPOSE_URL: &str =
    "https://github.com/immich-app/immich/releases/latest/download/docker-compose.yml";
const ENV_URL: &str = "https://github.com/immich-app/immich/releases/latest/download/example.env";

// ダウンロードに失敗した場合に使う同梱テンプレート
const COMPOSE_TEMPLATE: &str = r#"name: immich
services:
  immich-server:
    container_name: immich_server
    image: ghcr.io/immich-app/immich-server:${IMMICH_VERSION:-release}
    env_file: .env
    ports:
      - "__PORTS__"
    volumes:
      - ${UPLOAD_LOCATION}:/data
    depends_on:
      - redis
      - database
    restart: always

  immich-machine-learning:
    container_name: immich_machine_learning
    image: ghcr.io/immich-app/immich-machine-learning:${IMMICH_VERSION:-release}
    env_file: .env
    volumes:
      - model-cache:/cache
    restart: always

  redis:
    container_name: immich_redis
    image: docker.io/valkey/valkey:8-bookworm
    restart: always

  database:
    container_name: immich_postgres
    image: ghcr.io/immich-app/postgres:14-vectorchord0.3.0-pgvectors0.2.0
    environment:
      POSTGRES_PASSWORD: ${DB_PASSWORD}
      POSTGRES_USER: ${DB_USERNAME}
      POSTGRES_DB: ${DB_DATABASE_NAME}
      POSTGRES_INITDB_ARGS: '--data-checksums'
    volumes:
      - pgdata:/var/lib/postgresql/data
    restart: always

volumes:
  model-cache:
  pgdata:
"#;

#[derive(Clone, Copy, PartialEq)]
enum BindMode {
    LocalOnly,
    Lan,
}

#[derive(Default)]
struct Shared {
    docker_cli: bool,
    engine_up: bool,
    immich_state: String,
    tailscale_cli: bool,
    tailscale_dns: String,
    busy: bool,
    log: String,
}

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([820.0, 680.0])
            .with_min_inner_size([640.0, 480.0]),
        ..Default::default()
    };
    eframe::run_native(
        "Immich ランチャー",
        options,
        Box::new(|cc| Ok(Box::new(App::new(cc)))),
    )
}

struct App {
    shared: Arc<Mutex<Shared>>,
    bind: BindMode,
}

impl App {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        install_jp_fonts(&cc.egui_ctx);
        let shared = Arc::new(Mutex::new(Shared {
            immich_state: "-".into(),
            log: "ようこそ。初回は「① セットアップ」→「② Immich 起動」の順に押してください。\n".into(),
            ..Default::default()
        }));
        spawn_status_thread(shared.clone(), cc.egui_ctx.clone());
        Self {
            shared,
            bind: BindMode::LocalOnly,
        }
    }
}

// ---------- パス ----------

fn config_dir() -> PathBuf {
    PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into()))
        .join("ImmichLauncher")
}

fn library_dir() -> PathBuf {
    PathBuf::from(std::env::var("USERPROFILE").unwrap_or_else(|_| ".".into())).join("ImmichLibrary")
}

fn docker_bin() -> Option<String> {
    if which_ok("docker") {
        Some("docker".into())
    } else if std::path::Path::new(DOCKER_CLI_FALLBACK).exists() {
        Some(DOCKER_CLI_FALLBACK.into())
    } else {
        None
    }
}

fn tailscale_bin() -> Option<String> {
    if which_ok("tailscale") {
        Some("tailscale".into())
    } else if std::path::Path::new(TAILSCALE_FALLBACK).exists() {
        Some(TAILSCALE_FALLBACK.into())
    } else {
        None
    }
}

fn which_ok(name: &str) -> bool {
    Command::new("where.exe")
        .arg(name)
        .creation_flags(CREATE_NO_WINDOW)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false)
}

// ---------- 小さなコマンド実行 ----------

fn quiet(program: &str, args: &[&str]) -> Option<String> {
    let out = Command::new(program)
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .output()
        .ok()?;
    if out.status.success() {
        Some(String::from_utf8_lossy(&out.stdout).trim().to_string())
    } else {
        None
    }
}

fn fire_and_forget(program: &str, args: &[&str]) {
    let _ = Command::new(program)
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn();
}

fn open_url(url: &str) {
    fire_and_forget("cmd.exe", &["/C", "start", "", url]);
}

// ---------- 状態監視スレッド ----------

fn spawn_status_thread(shared: Arc<Mutex<Shared>>, ctx: egui::Context) {
    std::thread::spawn(move || loop {
        let docker = docker_bin();
        let mut engine = false;
        let mut state = "-".to_string();
        if let Some(bin) = &docker {
            engine = quiet(bin, &["info", "--format", "{{.ServerVersion}}"]).is_some();
            if engine {
                state = quiet(bin, &["inspect", "-f", "{{.State.Status}}", "immich_server"])
                    .unwrap_or_else(|| "未作成".into());
            }
        }
        let ts = tailscale_bin();
        let mut dns = String::new();
        if let Some(bin) = &ts {
            if let Some(json) = quiet(bin, &["status", "--json"]) {
                dns = extract_json_str(&json, "\"DNSName\":\"")
                    .trim_end_matches('.')
                    .to_string();
            }
        }
        {
            let mut s = shared.lock().unwrap();
            s.docker_cli = docker.is_some();
            s.engine_up = engine;
            s.immich_state = state;
            s.tailscale_cli = ts.is_some();
            s.tailscale_dns = dns;
        }
        ctx.request_repaint();
        std::thread::sleep(Duration::from_secs(4));
    });
}

fn extract_json_str<'a>(json: &'a str, key: &str) -> &'a str {
    if let Some(i) = json.find(key) {
        let rest = &json[i + key.len()..];
        if let Some(j) = rest.find('"') {
            return &rest[..j];
        }
    }
    ""
}

// ---------- ログ ----------

fn append(shared: &Arc<Mutex<Shared>>, ctx: &egui::Context, text: &str) {
    let mut s = shared.lock().unwrap();
    s.log.push_str(text);
    if s.log.len() > 300_000 {
        let mut cut = s.log.len() - 200_000;
        while !s.log.is_char_boundary(cut) {
            cut += 1;
        }
        s.log = s.log[cut..].to_string();
    }
    drop(s);
    ctx.request_repaint();
}

// ---------- バックグラウンドタスク ----------

fn spawn_task<F>(shared: &Arc<Mutex<Shared>>, ctx: &egui::Context, title: &str, f: F)
where
    F: FnOnce(&Arc<Mutex<Shared>>, &egui::Context) + Send + 'static,
{
    {
        let mut s = shared.lock().unwrap();
        if s.busy {
            s.log.push_str("[別の処理が実行中です。完了を待ってください]\n");
            return;
        }
        s.busy = true;
        s.log.push_str(&format!("\n===== {} =====\n", title));
    }
    ctx.request_repaint();
    let shared = shared.clone();
    let ctx = ctx.clone();
    std::thread::spawn(move || {
        f(&shared, &ctx);
        shared.lock().unwrap().busy = false;
        ctx.request_repaint();
    });
}

/// コマンドを実行し、標準出力/標準エラーを逐次ログへ流す(ブロッキング)
fn stream_blocking(
    shared: &Arc<Mutex<Shared>>,
    ctx: &egui::Context,
    program: &str,
    args: &[&str],
    cwd: Option<&PathBuf>,
) -> bool {
    append(shared, ctx, &format!("> {} {}\n", program, args.join(" ")));
    let mut cmd = Command::new(program);
    cmd.args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(d) = cwd {
        cmd.current_dir(d);
    }
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            append(shared, ctx, &format!("[起動失敗: {}: {}]\n", program, e));
            return false;
        }
    };
    let out = child.stdout.take().unwrap();
    let err = child.stderr.take().unwrap();
    let (s2, c2) = (shared.clone(), ctx.clone());
    let t = std::thread::spawn(move || pipe_stream(err, &s2, &c2));
    pipe_stream(out, shared, ctx);
    let _ = t.join();
    let ok = child.wait().map(|s| s.success()).unwrap_or(false);
    append(shared, ctx, if ok { "[完了]\n" } else { "[エラー終了]\n" });
    ok
}

fn pipe_stream<R: Read>(r: R, shared: &Arc<Mutex<Shared>>, ctx: &egui::Context) {
    let mut reader = BufReader::new(r);
    let mut buf = Vec::new();
    loop {
        buf.clear();
        match std::io::BufRead::read_until(&mut reader, b'\n', &mut buf) {
            Ok(0) | Err(_) => break,
            Ok(_) => append(shared, ctx, &String::from_utf8_lossy(&buf)),
        }
    }
}

// ---------- セットアップ ----------

fn download(url: &str) -> Option<String> {
    let out = Command::new("curl.exe")
        .args(["-fsSL", "--max-time", "20", url])
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .output()
        .ok()?;
    if out.status.success() && !out.stdout.is_empty() {
        Some(String::from_utf8_lossy(&out.stdout).to_string())
    } else {
        None
    }
}

fn random_password() -> String {
    rand::thread_rng()
        .sample_iter(&Alphanumeric)
        .take(24)
        .map(char::from)
        .collect()
}

/// 公式 docker-compose.yml を取得して安全化パッチを当てる。失敗時は同梱テンプレート。
fn build_compose(bind: BindMode) -> (String, &'static str) {
    let ports = match bind {
        BindMode::LocalOnly => "127.0.0.1:2283:2283",
        BindMode::Lan => "2283:2283",
    };
    if let Some(official) = download(COMPOSE_URL) {
        let mut c = official.replace("127.0.0.1:2283:2283", "2283:2283");
        if bind == BindMode::LocalOnly {
            c = c.replace("2283:2283", "127.0.0.1:2283:2283");
        }
        // Windows では Postgres のバインドマウントが不安定なため名前付きボリュームに置換
        c = c.replace(
            "${DB_DATA_LOCATION}:/var/lib/postgresql/data",
            "pgdata:/var/lib/postgresql/data",
        );
        if c.trim_end().ends_with("model-cache:") && c.contains("immich-server") {
            c = format!("{}\n  pgdata:\n", c.trim_end());
            return (c, "公式最新版をダウンロードして適用");
        }
    }
    (
        COMPOSE_TEMPLATE.replace("__PORTS__", ports),
        "同梱テンプレートを適用(ダウンロード不可のため)",
    )
}

fn build_env() -> String {
    let lib = library_dir().display().to_string();
    let base = download(ENV_URL).unwrap_or_else(|| {
        "UPLOAD_LOCATION=./library\nIMMICH_VERSION=release\nDB_PASSWORD=postgres\nDB_USERNAME=postgres\nDB_DATABASE_NAME=immich\n".into()
    });
    let mut out = String::new();
    let mut has_tz = false;
    for line in base.lines() {
        if line.starts_with("UPLOAD_LOCATION=") {
            out.push_str(&format!("UPLOAD_LOCATION={}\n", lib));
        } else if line.starts_with("DB_PASSWORD=") {
            out.push_str(&format!("DB_PASSWORD={}\n", random_password()));
        } else if line.starts_with("TZ=") || line.starts_with("# TZ=") {
            out.push_str("TZ=Asia/Tokyo\n");
            has_tz = true;
        } else {
            out.push_str(line);
            out.push('\n');
        }
    }
    if !has_tz {
        out.push_str("TZ=Asia/Tokyo\n");
    }
    out
}

fn do_setup(shared: &Arc<Mutex<Shared>>, ctx: &egui::Context, bind: BindMode) -> bool {
    let dir = config_dir();
    let lib = library_dir();
    if let Err(e) = std::fs::create_dir_all(&dir).and(std::fs::create_dir_all(&lib)) {
        append(shared, ctx, &format!("[フォルダ作成失敗: {}]\n", e));
        return false;
    }
    let (compose, how) = build_compose(bind);
    if let Err(e) = std::fs::write(dir.join("docker-compose.yml"), compose) {
        append(shared, ctx, &format!("[compose 書き込み失敗: {}]\n", e));
        return false;
    }
    append(shared, ctx, &format!("docker-compose.yml: {}\n", how));
    let env_path = dir.join(".env");
    if env_path.exists() {
        append(shared, ctx, ".env: 既存の設定(パスワード等)を維持\n");
    } else {
        if let Err(e) = std::fs::write(&env_path, build_env()) {
            append(shared, ctx, &format!("[.env 書き込み失敗: {}]\n", e));
            return false;
        }
        append(shared, ctx, ".env: 新規作成(DB パスワードは自動生成)\n");
    }
    append(
        shared,
        ctx,
        &format!(
            "設定フォルダ: {}\n写真の保存先: {}\nバインド: {}\n",
            dir.display(),
            lib.display(),
            match bind {
                BindMode::LocalOnly => "127.0.0.1 (このPCのみ・ポート解放不要)",
                BindMode::Lan => "0.0.0.0 (同じLANの端末からも接続可)",
            }
        ),
    );
    true
}

fn ensure_engine(shared: &Arc<Mutex<Shared>>, ctx: &egui::Context, docker: &str) -> bool {
    if quiet(docker, &["info", "--format", "{{.ServerVersion}}"]).is_some() {
        return true;
    }
    if std::path::Path::new(DOCKER_DESKTOP_EXE).exists() {
        append(shared, ctx, "Docker Desktop を起動しています(最大2分待機)…\n");
        fire_and_forget(DOCKER_DESKTOP_EXE, &[]);
        for _ in 0..40 {
            std::thread::sleep(Duration::from_secs(3));
            if quiet(docker, &["info", "--format", "{{.ServerVersion}}"]).is_some() {
                append(shared, ctx, "Docker エンジン起動を確認しました。\n");
                return true;
            }
        }
    }
    append(
        shared,
        ctx,
        "[Docker エンジンに接続できません。Docker Desktop を起動してから再試行してください]\n",
    );
    false
}

// ---------- UI ----------

fn install_jp_fonts(ctx: &egui::Context) {
    for path in [
        r"C:\Windows\Fonts\YuGothM.ttc",
        r"C:\Windows\Fonts\meiryo.ttc",
        r"C:\Windows\Fonts\msgothic.ttc",
    ] {
        if let Ok(bytes) = std::fs::read(path) {
            let mut fonts = egui::FontDefinitions::default();
            fonts
                .font_data
                .insert("jp".into(), egui::FontData::from_owned(bytes));
            for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
                fonts.families.get_mut(&family).unwrap().push("jp".into());
            }
            ctx.set_fonts(fonts);
            break;
        }
    }
}

fn dot(ui: &mut egui::Ui, ok: bool, label: &str) {
    let color = if ok {
        egui::Color32::from_rgb(0, 170, 90)
    } else {
        egui::Color32::from_rgb(200, 60, 60)
    };
    ui.colored_label(color, "●");
    ui.label(label);
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        let (docker_cli, engine_up, immich_state, ts_cli, ts_dns, busy, log) = {
            let s = self.shared.lock().unwrap();
            (
                s.docker_cli,
                s.engine_up,
                s.immich_state.clone(),
                s.tailscale_cli,
                s.tailscale_dns.clone(),
                s.busy,
                s.log.clone(),
            )
        };
        let shared = &self.shared;
        let running = immich_state == "running";

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.heading("Immich ランチャー");
            ui.label("ポート解放なしで安全に Immich を運用します(既定は 127.0.0.1 のみにバインド)。");
            ui.add_space(6.0);

            ui.horizontal(|ui| {
                dot(ui, docker_cli, "Docker");
                ui.separator();
                dot(ui, engine_up, "エンジン");
                ui.separator();
                dot(ui, running, &format!("Immich: {}", immich_state));
                ui.separator();
                dot(
                    ui,
                    ts_cli,
                    &if ts_dns.is_empty() {
                        "Tailscale".to_string()
                    } else {
                        format!("Tailscale: {}", ts_dns)
                    },
                );
                if busy {
                    ui.separator();
                    ui.spinner();
                    ui.label("処理中…");
                }
            });
            ui.separator();

            // --- Docker 未導入時のガイド ---
            if !docker_cli {
                ui.label("Docker Desktop が見つかりません。まずインストールしてください(WSL2 も自動で構成されます)。");
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(!busy, egui::Button::new("winget でインストール"))
                        .clicked()
                    {
                        spawn_task(shared, ctx, "Docker Desktop インストール", |s, c| {
                            append(s, c, "UAC(管理者確認)ダイアログが出たら許可してください。\n");
                            stream_blocking(
                                s,
                                c,
                                "winget",
                                &[
                                    "install", "-e", "--id", "Docker.DockerDesktop",
                                    "--accept-source-agreements", "--accept-package-agreements",
                                ],
                                None,
                            );
                            append(s, c, "インストール後、一度 Windows の再起動が必要な場合があります。\n");
                        });
                    }
                    if ui.button("ダウンロードページを開く").clicked() {
                        open_url("https://www.docker.com/products/docker-desktop/");
                    }
                });
                ui.separator();
            }

            // --- 基本操作 ---
            let bind = self.bind;
            ui.horizontal(|ui| {
                if ui
                    .add_enabled(!busy, egui::Button::new("① セットアップ / 設定再生成"))
                    .clicked()
                {
                    spawn_task(shared, ctx, "セットアップ", move |s, c| {
                        do_setup(s, c, bind);
                    });
                }
                if ui.add_enabled(!busy, egui::Button::new("② Immich 起動")).clicked() {
                    spawn_task(shared, ctx, "Immich 起動", move |s, c| {
                        let dir = config_dir();
                        if !dir.join("docker-compose.yml").exists() && !do_setup(s, c, bind) {
                            return;
                        }
                        let Some(docker) = docker_bin() else {
                            append(s, c, "[Docker が見つかりません]\n");
                            return;
                        };
                        if !ensure_engine(s, c, &docker) {
                            return;
                        }
                        append(s, c, "初回はイメージ取得に数分かかります。\n");
                        if stream_blocking(s, c, &docker, &["compose", "up", "-d"], Some(&dir)) {
                            append(s, c, &format!("起動しました: {}\n", IMMICH_URL));
                        }
                    });
                }
                if ui.add_enabled(!busy, egui::Button::new("停止")).clicked() {
                    spawn_task(shared, ctx, "Immich 停止", |s, c| {
                        if let Some(docker) = docker_bin() {
                            stream_blocking(s, c, &docker, &["compose", "down"], Some(&config_dir()));
                        }
                    });
                }
                if ui.add_enabled(!busy, egui::Button::new("更新")).clicked() {
                    spawn_task(shared, ctx, "Immich 更新", |s, c| {
                        if let Some(docker) = docker_bin() {
                            let dir = config_dir();
                            if stream_blocking(s, c, &docker, &["compose", "pull"], Some(&dir)) {
                                stream_blocking(s, c, &docker, &["compose", "up", "-d"], Some(&dir));
                            }
                        }
                    });
                }
                if ui.add_enabled(!busy, egui::Button::new("ログ")).clicked() {
                    spawn_task(shared, ctx, "コンテナログ", |s, c| {
                        if let Some(docker) = docker_bin() {
                            stream_blocking(
                                s,
                                c,
                                &docker,
                                &["compose", "logs", "--tail", "100"],
                                Some(&config_dir()),
                            );
                        }
                    });
                }
            });
            ui.horizontal(|ui| {
                if ui.add_enabled(running, egui::Button::new("ブラウザで開く")).clicked() {
                    open_url(IMMICH_URL);
                }
                if ui.button("設定フォルダ").clicked() {
                    fire_and_forget("explorer.exe", &[&config_dir().display().to_string()]);
                }
                if ui.button("写真フォルダ").clicked() {
                    let _ = std::fs::create_dir_all(library_dir());
                    fire_and_forget("explorer.exe", &[&library_dir().display().to_string()]);
                }
            });

            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label("公開範囲:");
                ui.radio_value(&mut self.bind, BindMode::LocalOnly, "このPCのみ(推奨)");
                ui.radio_value(&mut self.bind, BindMode::Lan, "同じLANにも公開");
            });
            if self.bind == BindMode::Lan {
                ui.colored_label(
                    egui::Color32::from_rgb(200, 140, 0),
                    "LAN 公開時は同一ネットワークの全端末からアクセス可能になります。変更後は「セットアップ」→「起動」で反映。",
                );
            }

            ui.separator();

            // --- 外出先アクセス (Tailscale) ---
            egui::CollapsingHeader::new("外出先からのアクセス (Tailscale) — ポート解放不要")
                .default_open(true)
                .show(ui, |ui| {
                    ui.label("Tailscale の暗号化トンネルで、自分のアカウントのデバイスだけが HTTPS でアクセスできます。ルーター設定は不要です。");
                    ui.horizontal(|ui| {
                        if !ts_cli {
                            if ui
                                .add_enabled(!busy, egui::Button::new("Tailscale をインストール"))
                                .clicked()
                            {
                                spawn_task(shared, ctx, "Tailscale インストール", |s, c| {
                                    stream_blocking(
                                        s,
                                        c,
                                        "winget",
                                        &[
                                            "install", "-e", "--id", "tailscale.tailscale",
                                            "--accept-source-agreements", "--accept-package-agreements",
                                        ],
                                        None,
                                    );
                                });
                            }
                        } else {
                            if ui.add_enabled(!busy, egui::Button::new("ログイン")).clicked() {
                                spawn_task(shared, ctx, "Tailscale ログイン", |s, c| {
                                    if let Some(ts) = tailscale_bin() {
                                        append(s, c, "ブラウザが開いたらログインしてください。\n");
                                        stream_blocking(s, c, &ts, &["login"], None);
                                    }
                                });
                            }
                            if ui
                                .add_enabled(!busy, egui::Button::new("外出先アクセスを有効化"))
                                .clicked()
                            {
                                spawn_task(shared, ctx, "tailscale serve 有効化", |s, c| {
                                    if let Some(ts) = tailscale_bin() {
                                        if stream_blocking(
                                            s,
                                            c,
                                            &ts,
                                            &["serve", "--bg", "http://127.0.0.1:2283"],
                                            None,
                                        ) {
                                            append(s, c, "スマホの Immich アプリには上記の https:// の URL を入力してください。\n(初回は Tailscale 管理画面で MagicDNS と HTTPS の有効化が必要な場合があります)\n");
                                        }
                                    }
                                });
                            }
                            if ui.add_enabled(!busy, egui::Button::new("無効化")).clicked() {
                                spawn_task(shared, ctx, "tailscale serve 無効化", |s, c| {
                                    if let Some(ts) = tailscale_bin() {
                                        stream_blocking(s, c, &ts, &["serve", "reset"], None);
                                    }
                                });
                            }
                            if ui.add_enabled(!busy, egui::Button::new("公開状態を確認")).clicked() {
                                spawn_task(shared, ctx, "tailscale serve 状態", |s, c| {
                                    if let Some(ts) = tailscale_bin() {
                                        stream_blocking(s, c, &ts, &["serve", "status"], None);
                                        stream_blocking(s, c, &ts, &["status"], None);
                                    }
                                });
                            }
                        }
                    });
                    if !ts_dns.is_empty() {
                        ui.label(format!("外出先用 URL: https://{}", ts_dns));
                    }
                });

            ui.separator();
            ui.label("ログ:");
            egui::ScrollArea::vertical()
                .stick_to_bottom(true)
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    ui.add(
                        egui::Label::new(egui::RichText::new(&log).monospace().size(12.0))
                            .wrap(),
                    );
                });
        });
    }
}
