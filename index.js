/**
 * Host half of the screenshot bundle.
 *
 * The region selector is a tiny compiled WinForms program: a browser overlay
 * can only cover the app window, while WeChat-style selection needs a native
 * full-screen window at 1:1. The exe ships beside this file and is rebuilt from
 * select.cs on demand, so an antivirus quarantine never bricks the feature.
 */
import { execFile, spawn } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'

const run = promisify(execFile)
const here = path.dirname(fileURLToPath(import.meta.url))

/** Where the exes actually run from — deliberately OUTSIDE this package. */
function binDir() {
  const base = process.env.LOCALAPPDATA
    ?? process.env.XDG_CACHE_HOME
    ?? path.join(os.homedir(), '.cache')
  return path.join(base, 'dsh-screenshot-hotkey', 'bin')
}

/**
 * Copy an exe out of the package directory and return the outside copy.
 *
 * Windows locks the whole directory that holds a running exe, so while
 * hotkey.exe runs from `node_modules\dsh-screenshot-hotkey` pnpm cannot replace
 * that package: every market install that touches this `file:` dependency dies
 * with `ERR_PNPM_EPERM ... rename '.../dsh-screenshot-hotkey_tmp_*'` (2026-11-12,
 * twice for @michengai/dsh-skills-manager and twice for the dsh-pet update).
 * Worse, pnpm deletes before it renames, so each failed attempt also strips a few
 * files out of the copy (that is how client.js and cordis.patch.yml went missing).
 *
 * The copy lives in a per-build subdirectory (`bin\hotkey-<size>-<mtime>\hotkey.exe`)
 * so the process keeps its normal name while a new build never has to overwrite a
 * running exe. Old copies are pruned; ones still in use are simply skipped.
 */
function relocate(exeName) {
  const src = path.join(here, exeName)
  if (!fs.existsSync(src)) return null
  const stat = fs.statSync(src)
  const stem = path.basename(exeName, '.exe')
  const root = binDir()
  const dir = path.join(root, `${stem}-${stat.size}-${Math.round(stat.mtimeMs)}`)
  fs.mkdirSync(dir, { recursive: true })
  const dst = path.join(dir, exeName)
  if (!fs.existsSync(dst)) fs.copyFileSync(src, dst)
  for (const name of fs.readdirSync(root)) {
    if (name === path.basename(dir) || !name.startsWith(`${stem}-`)) continue
    try {
      fs.rmSync(path.join(root, name), { recursive: true, force: true })
    } catch {
      // still running — the next start will clean it up
    }
  }
  return dst
}

function cscPath() {
  const root = process.env.SystemRoot ?? 'C:\\Windows'
  for (const arch of ['Framework64', 'Framework']) {
    const candidate = path.join(root, 'Microsoft.NET', arch, 'v4.0.30319', 'csc.exe')
    if (fs.existsSync(candidate)) return candidate
  }
  return null
}

async function ensureSelector() {
  const shipped = relocate('shot.exe')
  if (shipped) return shipped
  const csc = cscPath()
  if (!csc) throw new Error('shot.exe is missing and no C# compiler was found')
  const exe = path.join(binDir(), 'shot-rebuilt.exe')
  fs.mkdirSync(binDir(), { recursive: true })
  await run(csc, [
    '/nologo', '/target:winexe', '/optimize',
    `/out:${exe}`,
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    path.join(here, 'select.cs'),
  ], { windowsHide: true, timeout: 120000 })
  if (!fs.existsSync(exe)) throw new Error('selector rebuild produced no binary')
  return exe
}

/**
 * Run the native selector and return the cropped PNG as a data URL.
 * Resolves null when the user cancels (Esc, or a drag too small to count).
 */
async function selectRegion(probe = false, theme = null) {
  const exe = await ensureSelector()
  const target = path.join(os.tmpdir(), `dsh-shot-${process.pid}-${Date.now()}.png`)
  const args = probe ? [target, '--auto', '400', '300'] : [target]
  if (!probe && theme) args.push('--theme', theme)
  const started = Date.now()
  try {
    await run(exe, args, { windowsHide: true, timeout: 600000, maxBuffer: 1024 * 1024 })
    const bytes = fs.readFileSync(target)
    if (!bytes.length) throw new Error('selector wrote an empty image')
    return { dataUrl: `data:image/png;base64,${bytes.toString('base64')}`, ms: Date.now() - started }
  } catch (error) {
    const code = error && typeof error === 'object' ? error.code : undefined
    // 2 = cancelled, 4 = saved to a file only (pure export): both hand nothing
    // back to the page, so it must not paste anything.
    if (code === 2 || code === 4) return null
    throw error
  } finally {
    fs.rm(target, { force: true }, () => {})
  }
}

/**
 * Build (once) and return the global-hotkey helper from outside the package.
 * Mirrors ensureSelector(): the exe is rebuilt from source when missing, so an
 * antivirus quarantine cannot permanently disable the shortcut.
 */
async function ensureHotkeyHelper() {
  const shipped = relocate('hotkey.exe')
  if (shipped) return shipped
  const csc = cscPath()
  if (!csc) throw new Error('hotkey.exe is missing and no C# compiler was found')
  const exe = path.join(binDir(), 'hotkey-rebuilt.exe')
  fs.mkdirSync(binDir(), { recursive: true })
  await run(csc, [
    '-nologo', '-target:winexe', '-optimize',
    `-out:${exe}`,
    path.join(here, 'hotkey.cs'),
  ], { windowsHide: true, timeout: 120000 })
  if (!fs.existsSync(exe)) throw new Error('hotkey helper build produced no binary')
  return exe
}

/** Queue where the hotkey helper drops finished captures for this page to pick up. */
const queueDir = path.join(os.tmpdir(), 'dsh-shot-queue')

const COMMAND_ID = 'dsh-screenshot.capture'
const DEFAULT_BINDING = { code: 'KeyS', modifiers: ['primary', 'alt'] }
/** Where the desktop shell persists the user's rebound shortcuts. */
const keybindingsPath = path.join(
  process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming'),
  '@deepseek-ai', 'dsh-desktop', 'keybindings.json',
)

/**
 * Read the binding the user currently has for this command.
 * The shortcut panel writes overrides here, so the global hotkey follows a rebind
 * instead of being pinned to the original default.
 */
function currentBinding(platform) {
  try {
    const document = JSON.parse(fs.readFileSync(keybindingsPath, 'utf8'))
    const override = document?.profiles?.[`desktop:${platform}`]?.[COMMAND_ID]
    if (override && typeof override.code === 'string' && Array.isArray(override.modifiers)) {
      return { code: override.code, modifiers: override.modifiers }
    }
  } catch {
    /* no override yet, or the file is mid-write: fall back to the default */
  }
  return DEFAULT_BINDING
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

/**
 * Take the oldest finished capture out of the queue.
 * The helper writes `<id>.json` pointing at the PNG; both files are consumed here,
 * so a capture is delivered exactly once.
 */
function takeQueuedCapture() {
  let names
  try {
    names = fs.readdirSync(queueDir).filter((name) => name.endsWith('.json')).sort()
  } catch {
    return null
  }
  for (const name of names) {
    const metaPath = path.join(queueDir, name)
    try {
      const meta = JSON.parse(fs.readFileSync(metaPath, 'utf8'))
      fs.rmSync(metaPath, { force: true })
      const pngPath = typeof meta.png === 'string' ? meta.png : null
      if (!pngPath || !fs.existsSync(pngPath)) continue
      const bytes = fs.readFileSync(pngPath)
      fs.rmSync(pngPath, { force: true })
      if (!bytes.length) continue
      return `data:image/png;base64,${bytes.toString('base64')}`
    } catch {
      fs.rmSync(metaPath, { force: true })
    }
  }
  return null
}

/**
 * Start the system-wide listener that runs shot.exe without touching the DSH window.
 * The helper also watches this Host's pid, so a hard crash still ends it.
 */
function startHotkeyHelper(ctx) {
  const log = (error) => ctx.logger?.('screenshot')?.warn?.(error)
  const platform = process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'macos' : 'linux'
  let child = null
  let exePath = null
  let cancelled = false
  let restartTimer = null
  fs.mkdirSync(queueDir, { recursive: true })

  // The helper and the selector it launches both live outside the package.
  const shotExe = () => relocate('shot.exe') ?? path.join(here, 'shot.exe')

  const spawnHelper = () => {
    if (cancelled || exePath === null) return
    const binding = currentBinding(platform)
    child = spawn(exePath, [
      String(process.pid),
      shotExe(),
      queueDir,
      binding.code,
      (binding.modifiers ?? []).join(','),
    ], { windowsHide: true, stdio: 'ignore' })
    child.on('error', log)
    child.on('exit', (code) => {
      if (!cancelled && code !== 0) log(new Error(`global hotkey helper exited with ${String(code)}`))
    })
  }

  ensureHotkeyHelper().then((exe) => {
    exePath = exe
    spawnHelper()
  }).catch(log)

  // 用户在「快捷键」面板改键 → 重读 keybindings.json → 重启小程序换键位。
  // 面板写文件不是原子的，所以去抖 400ms 再重启。
  let watcher = null
  try {
    watcher = fs.watch(path.dirname(keybindingsPath), (_event, filename) => {
      if (filename !== 'keybindings.json') return
      clearTimeout(restartTimer)
      restartTimer = setTimeout(() => {
        const previous = child
        child = null
        previous?.kill()
        spawnHelper()
      }, 400)
    })
  } catch (error) {
    log(error)
  }

  return () => {
    cancelled = true
    clearTimeout(restartTimer)
    watcher?.close()
    child?.kill()
  }
}

function send(res, status, body) {
  const payload = JSON.stringify(body)
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(payload),
    'Cache-Control': 'no-store',
  })
  res.end(payload)
}

export function apply(ctx, config = {}) {
  const route = config.route ?? '/shot/capture'

  ctx.inject(['webServer'], (webCtx) => {
    webCtx.effect(() => webCtx.webServer.register({
      kind: 'exact',
      path: route,
      handler: async (req, res) => {
        // A custom header keeps a stray cross-origin <img>/fetch from being a
        // simple request; the value is a marker, not a secret.
        if (req.method !== 'GET') return send(res, 405, { error: 'method not allowed' })
        if (!req.headers['x-dsh-shot']) return send(res, 403, { error: 'missing x-dsh-shot header' })
        try {
          let probe = false
          try {
            probe = new URL(req.url ?? '/', 'http://127.0.0.1').searchParams.get('probe') === '1'
          } catch {
            /* keep the default */
          }
          const result = await selectRegion(probe, config.theme === 'dark' ? 'dark' : null)
          if (!result) {
            res.writeHead(204, { 'Cache-Control': 'no-store' })
            res.end()
            return
          }
          send(res, 200, result)
        } catch (error) {
          ctx.logger?.('screenshot')?.warn?.(error)
          send(res, 500, { error: String((error && error.message) || error) })
        }
      },
    }), 'screenshot: capture route')

    // 页面长轮询这只队列：热键小程序在「DSH 不在前台」时直接截图，图放这里，
    // 由页面取走并贴进输入框。请求最多挂 25 秒，客户端断了就提前退出。
    webCtx.effect(() => webCtx.webServer.register({
      kind: 'exact',
      path: '/shot/pending',
      handler: async (req, res) => {
        let wait = 20000
        try {
          const raw = new URL(req.url ?? '/', 'http://127.0.0.1').searchParams.get('wait')
          const parsed = Number(raw)
          if (Number.isFinite(parsed)) wait = Math.max(0, Math.min(parsed, 25000))
        } catch {
          /* keep the default wait */
        }
        const deadline = Date.now() + wait
        for (;;) {
          const dataUrl = takeQueuedCapture()
          if (dataUrl !== null) return send(res, 200, { dataUrl })
          if (res.destroyed) return
          if (Date.now() >= deadline) {
            res.writeHead(204, { 'Cache-Control': 'no-store' })
            res.end()
            return
          }
          await sleep(250)
        }
      },
    }), 'screenshot: pending intake')
  })

  // 全局热键：页面里的监听只管 DSH 在前台的情况，这个常驻小程序补上「别的窗口在前台」。
  ctx.effect(() => startHotkeyHelper(ctx), 'screenshot: global hotkey')
}
