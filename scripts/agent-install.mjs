#!/usr/bin/env node
/**
 * PixelPair agent-driven installer.
 * Downloads (or builds) the app into a standard location and registers MCP.
 *
 * Usage:
 *   node scripts/agent-install.mjs --client cursor
 *   node scripts/agent-install.mjs --check
 *   node scripts/agent-install.mjs --print --client cursor
 */
import { spawn, spawnSync } from "node:child_process";
import {
  chmod,
  copyFile,
  mkdir,
  readFile,
  access,
  rm,
  writeFile,
} from "node:fs/promises";
import { readFileSync, createWriteStream, existsSync } from "node:fs";
import { homedir, platform, arch, tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { pipeline } from "node:stream/promises";

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = join(__dirname, "..");
const GITHUB_REPO = "ddunddun-labs/PixelPair";
const RELEASE_API = `https://api.github.com/repos/${GITHUB_REPO}/releases/latest`;
const SERVER_NAME = "pixelpair";

const PLATFORM_RID = {
  "win32-x64": "win-x64",
  "linux-x64": "linux-x64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64",
};

function parseArgs(argv) {
  const opts = {
    client: "cursor",
    preferRelease: true,
    fromSource: false,
    force: false,
    print: false,
    check: false,
    installMcp: true,
    start: true,
  };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--client" && argv[i + 1]) opts.client = argv[++i];
    else if (arg === "--prefer-release") opts.preferRelease = true;
    else if (arg === "--from-source") {
      opts.fromSource = true;
      opts.preferRelease = false;
    } else if (arg === "--force") opts.force = true;
    else if (arg === "--print") opts.print = true;
    else if (arg === "--check") opts.check = true;
    else if (arg === "--no-mcp") opts.installMcp = false;
    else if (arg === "--no-start") opts.start = false;
    else if (arg === "--help" || arg === "-h") opts.help = true;
  }
  return opts;
}

function printHelp() {
  console.log(`PixelPair agent installer

Usage:
  node scripts/agent-install.mjs [options]

Options:
  --client cursor|claude|codex   MCP client (default: cursor)
  --prefer-release               Download latest GitHub Release (default)
  --from-source                  dotnet publish instead of release download
  --force                        Overwrite existing install
  --print                        Print MCP config; do not install
  --check                        Verify install and server health
  --no-mcp                       Skip MCP registration
  --no-start                     Do not start PixelPair after install
  --help                         Show this help
`);
}

function getPlatformKey() {
  return `${platform()}-${arch()}`;
}

function installerSelfCommand(subcommand = "") {
  if (existsSync(join(REPO_ROOT, "PixelPair.csproj"))) {
    return `node scripts/agent-install.mjs${subcommand ? ` ${subcommand}` : ""}`;
  }
  if (existsSync(join(REPO_ROOT, "agent-install.mjs"))) {
    return `node agent-install.mjs${subcommand ? ` ${subcommand}` : ""}`;
  }
  return `node scripts/agent-install.mjs${subcommand ? ` ${subcommand}` : ""}`;
}

function getPlatformRid() {
  const rid = PLATFORM_RID[getPlatformKey()];
  if (!rid) {
    throw new Error(`Unsupported platform: ${getPlatformKey()}`);
  }
  return rid;
}

function getInstallRoot() {
  if (platform() === "win32") {
    const base = process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local");
    return join(base, "PixelPair");
  }
  return join(homedir(), ".pixelpair");
}

function getBinaryRelPath(rid) {
  return rid === "win-x64" ? join(rid, "PixelPair.exe") : join(rid, "PixelPair");
}

function getMcpPath(installRoot) {
  return join(installRoot, "mcp", "pixelpair-mcp.mjs");
}

function getCursorConfigPath() {
  return join(homedir(), ".cursor", "mcp.json");
}

function isWsl() {
  if (platform() !== "linux") return false;
  try {
    return /microsoft/i.test(readFileSync("/proc/version", "utf8"));
  } catch {
    return false;
  }
}

function runSync(cmd, args, options = {}) {
  const result = spawnSync(cmd, args, {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    ...options,
  });
  return result;
}

function commandExists(cmd) {
  const checker = platform() === "win32" ? "where" : "which";
  const result = runSync(checker, [cmd]);
  return result.status === 0;
}

function checkNodeVersion() {
  const major = Number(process.version.slice(1).split(".")[0]);
  if (major < 18) {
    throw new Error(`Node.js 18+ required (current: ${process.version})`);
  }
}

async function fetchJson(url) {
  const res = await fetch(url, {
    headers: { Accept: "application/vnd.github+json", "User-Agent": "pixelpair-agent-install" },
  });
  if (!res.ok) throw new Error(`GitHub API ${res.status}: ${url}`);
  return res.json();
}

async function downloadFile(url, destPath) {
  const res = await fetch(url, { redirect: "follow" });
  if (!res.ok) throw new Error(`Download failed ${res.status}: ${url}`);
  await pipeline(res.body, createWriteStream(destPath));
}

async function extractZip(zipPath, destDir) {
  if (platform() === "win32") {
    const ps = `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; Expand-Archive -LiteralPath '${zipPath.replace(/'/g, "''")}' -DestinationPath '${destDir.replace(/'/g, "''")}' -Force`;
    const result = runSync("powershell", ["-NoProfile", "-Command", ps]);
    if (result.status !== 0) {
      throw new Error(`Expand-Archive failed: ${result.stderr || result.stdout}`);
    }
    return;
  }
  if (!commandExists("unzip")) {
    throw new Error("unzip command not found; install unzip or use --from-source on Windows");
  }
  const result = runSync("unzip", ["-o", zipPath, "-d", destDir]);
  if (result.status !== 0) {
    throw new Error(`unzip failed: ${result.stderr || result.stdout}`);
  }
}

async function readInstallManifest(installRoot) {
  try {
    return JSON.parse(await readFile(join(installRoot, "install.json"), "utf8"));
  } catch {
    return null;
  }
}

async function writeInstallManifest(installRoot, data) {
  await writeFile(join(installRoot, "install.json"), JSON.stringify(data, null, 2) + "\n", "utf8");
}

async function installFromRelease(installRoot, rid, force) {
  const release = await fetchJson(RELEASE_API);
  const assetName = `PixelPair-${rid}.zip`;
  const asset = release.assets?.find((a) => a.name === assetName);
  if (!asset) {
    throw new Error(`Release asset not found: ${assetName} (tag ${release.tag_name})`);
  }

  if (force) {
    await rm(installRoot, { recursive: true, force: true });
  }
  await mkdir(installRoot, { recursive: true });

  const tmpZip = join(tmpdir(), `pixelpair-${rid}-${Date.now()}.zip`);
  try {
    process.stderr.write(`Downloading ${asset.name} (${release.tag_name})...\n`);
    await downloadFile(asset.browser_download_url, tmpZip);
    await extractZip(tmpZip, installRoot);
    const binPath = join(installRoot, getBinaryRelPath(rid));
    if (platform() !== "win32") {
      await chmod(binPath, 0o755);
    }
    await writeInstallManifest(installRoot, {
      version: release.tag_name,
      rid,
      source: "release",
      installedAt: new Date().toISOString(),
    });
    return { version: release.tag_name, binPath, mcpPath: getMcpPath(installRoot) };
  } finally {
    await rm(tmpZip, { force: true });
  }
}

async function installFromSource(installRoot, rid, force) {
  if (!commandExists("dotnet")) {
    throw new Error("dotnet SDK not found; use --prefer-release or install .NET 10 SDK");
  }
  if (force) {
    await rm(installRoot, { recursive: true, force: true });
  }
  await mkdir(installRoot, { recursive: true });

  const outDir = join(installRoot, rid);
  process.stderr.write(`Building PixelPair (${rid})...\n`);
  const result = runSync(
    "dotnet",
    [
      "publish",
      join(REPO_ROOT, "PixelPair.csproj"),
      "-c",
      "Release",
      "-r",
      rid,
      "--self-contained",
      "true",
      "-p:PublishSingleFile=true",
      "-o",
      outDir,
    ],
    { cwd: REPO_ROOT },
  );
  if (result.status !== 0) {
    throw new Error(`dotnet publish failed:\n${result.stderr || result.stdout}`);
  }

  await mkdir(join(installRoot, "mcp"), { recursive: true });
  await copyFile(join(REPO_ROOT, "mcp", "pixelpair-mcp.mjs"), getMcpPath(installRoot));

  const binPath = join(installRoot, getBinaryRelPath(rid));
  if (platform() !== "win32") {
    await chmod(binPath, 0o755);
  }
  await writeInstallManifest(installRoot, {
    version: "source",
    rid,
    source: "dotnet-publish",
    installedAt: new Date().toISOString(),
  });
  return { version: "source", binPath, mcpPath: getMcpPath(installRoot) };
}

function buildMcpEntry(mcpPath) {
  return {
    command: "node",
    args: [mcpPath],
  };
}

async function readJson(path, fallback) {
  try {
    return JSON.parse(await readFile(path, "utf8"));
  } catch {
    return fallback;
  }
}

async function installCursorMcp(mcpPath) {
  const configPath = getCursorConfigPath();
  await mkdir(dirname(configPath), { recursive: true });
  const config = await readJson(configPath, { mcpServers: {} });
  if (!config.mcpServers) config.mcpServers = {};
  config.mcpServers[SERVER_NAME] = buildMcpEntry(mcpPath);
  await writeFile(configPath, JSON.stringify(config, null, 2) + "\n", "utf8");
  return { configPath, method: "file" };
}

async function installClaudeMcp(mcpPath) {
  if (!commandExists("claude")) {
    return { ok: false, reason: "claude CLI not found; use --print for manual config" };
  }
  runSync("claude", ["mcp", "remove", SERVER_NAME]);
  const result = runSync("claude", [
    "mcp",
    "add",
    "--scope",
    "user",
    SERVER_NAME,
    "--",
    "node",
    mcpPath,
  ]);
  if (result.status !== 0) {
    return { ok: false, reason: result.stderr || result.stdout };
  }
  return { ok: true, method: "claude mcp add --scope user" };
}

async function installCodexMcp(mcpPath) {
  if (!commandExists("codex")) {
    return { ok: false, reason: "codex CLI not found; use --print for manual config" };
  }
  runSync("codex", ["mcp", "remove", SERVER_NAME]);
  const result = runSync("codex", ["mcp", "add", SERVER_NAME, "--", "node", mcpPath]);
  if (result.status !== 0) {
    return { ok: false, reason: result.stderr || result.stdout };
  }
  return { ok: true, method: "codex mcp add" };
}

async function registerMcp(client, mcpPath) {
  switch (client) {
    case "cursor":
      return { ok: true, ...(await installCursorMcp(mcpPath)) };
    case "claude":
      return installClaudeMcp(mcpPath);
    case "codex":
      return installCodexMcp(mcpPath);
    default:
      throw new Error(`Unknown client: ${client}`);
  }
}

async function resolveAgentUrl(installRoot) {
  const candidates = [
    join(installRoot, "agent-url.txt"),
    join(homedir(), ".pixelpair", "agent-url.txt"),
    process.env.LOCALAPPDATA
      ? join(process.env.LOCALAPPDATA, "PixelPair", "agent-url.txt")
      : "",
  ].filter(Boolean);
  for (const file of candidates) {
    try {
      const text = (await readFile(file, "utf8")).trim();
      if (text.startsWith("http")) return text.replace(/\/$/, "");
    } catch {
      /* skip */
    }
  }
  return "http://127.0.0.1:17890";
}

async function checkHealth(baseUrl) {
  try {
    const res = await fetch(`${baseUrl}/health`, { signal: AbortSignal.timeout(2000) });
    return res.ok;
  } catch {
    return false;
  }
}

async function startServer(binPath, installRoot) {
  const child = spawn(binPath, [], { detached: true, stdio: "ignore" });
  child.unref();
  for (let i = 0; i < 40; i++) {
    await new Promise((r) => setTimeout(r, 250));
    const base = await resolveAgentUrl(installRoot);
    if (await checkHealth(base)) return true;
  }
  return false;
}

async function pathExists(path) {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function verifyInstall(installRoot, rid) {
  const mcpPath = getMcpPath(installRoot);
  const binPath = join(installRoot, getBinaryRelPath(rid));
  const manifest = await readInstallManifest(installRoot);
  const baseUrl = await resolveAgentUrl(installRoot);
  const healthy = await checkHealth(baseUrl);
  return {
    installRoot,
    mcpPath,
    binPath,
    manifest,
    baseUrl,
    serverHealthy: healthy,
    mcpScriptExists: await pathExists(mcpPath),
    binaryExists: await pathExists(binPath),
  };
}

function printSummary(result) {
  console.log(JSON.stringify(result, null, 2));
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) {
    printHelp();
    process.exit(0);
  }

  checkNodeVersion();
  const rid = getPlatformRid();
  const installRoot = getInstallRoot();

  if (isWsl()) {
    process.stderr.write(
      "Note: WSL detected — run PixelPair and MCP in the same WSL environment as your agent.\n",
    );
  }

  if (opts.print) {
    const mcpPath = getMcpPath(installRoot);
    printSummary({
      ok: true,
      mode: "print",
      client: opts.client,
      installRoot,
      mcp: buildMcpEntry(mcpPath),
      cursorConfigPath: getCursorConfigPath(),
      claudeCommand: `claude mcp add --scope user ${SERVER_NAME} -- node ${mcpPath}`,
      codexCommand: `codex mcp add ${SERVER_NAME} -- node ${mcpPath}`,
    });
    return;
  }

  if (opts.check) {
    const status = await verifyInstall(installRoot, rid);
    printSummary({
      ok: status.mcpScriptExists && status.binaryExists && status.serverHealthy,
      mode: "check",
      ...status,
    });
    process.exit(status.mcpScriptExists && status.binaryExists && status.serverHealthy ? 0 : 1);
  }

  const existing = await readInstallManifest(installRoot);
  if (existing && !opts.force) {
    process.stderr.write(`Install already exists at ${installRoot} (use --force to reinstall)\n`);
  } else {
    if (opts.fromSource) {
      await installFromSource(installRoot, rid, opts.force || !existing);
    } else {
      await installFromRelease(installRoot, rid, opts.force || !existing);
    }
  }

  const mcpPath = getMcpPath(installRoot);
  const binPath = join(installRoot, getBinaryRelPath(rid));

  let mcpResult = null;
  if (opts.installMcp) {
    mcpResult = await registerMcp(opts.client, mcpPath);
  }

  let serverStarted = false;
  if (opts.start) {
    const baseUrl = await resolveAgentUrl(installRoot);
    if (!(await checkHealth(baseUrl))) {
      process.stderr.write("Starting PixelPair server...\n");
      serverStarted = await startServer(binPath, installRoot);
    } else {
      serverStarted = true;
    }
  }

  const status = await verifyInstall(installRoot, rid);
  const result = {
    ok: status.mcpScriptExists && status.binaryExists,
    mode: "install",
    client: opts.client,
    installRoot,
    mcpPath,
    binPath,
    mcpRegistration: mcpResult,
    serverHealthy: status.serverHealthy,
    serverStarted,
    restartRequired: opts.installMcp,
    nextSteps: [
      opts.installMcp ? "Restart the MCP client (Cursor / Claude Code / Codex) once." : null,
      !status.serverHealthy ? `Start PixelPair manually: ${binPath}` : null,
      `Verify: ${installerSelfCommand("--check")}`,
      "Then ask the agent to call MCP get_canvas or get_status.",
    ].filter(Boolean),
  };

  printSummary(result);
  process.exit(result.ok ? 0 : 1);
}

main().catch((err) => {
  console.error(JSON.stringify({ ok: false, error: err.message }, null, 2));
  process.exit(1);
});
