/**
 * dsh-tray-plugin host control: locating DshTray.exe, probing the tray icon
 * and the dsh service, spawning the tray / restart / exit actions, and the
 * loopback-fenced /api/dsh-tray route family.
 *
 * Security: every route is loopback-only (same as the dsh-ssh route family),
 * because restart/exit terminate the very process serving the request — a
 * LAN-exposed dsh web deployment must not expose these endpoints.
 */

import { spawn, execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createConnection } from 'node:net'
import type { IncomingMessage, ServerResponse } from 'node:http'
import type { WebRoute } from '@deepseek-ai/dsh-host-webserver'

/** The /api/dsh-tray route family prefix. */
export const TRAY_API = '/api/dsh-tray'

/** Effective plugin config the routes read (resolved by the host apply). */
export interface TrayConfig {
  /** Master switch for the plugin (routes). */
  enabled: boolean
  /** Absolute path to DshTray.exe (empty = auto-detect). */
  trayPath: string
  /** Port the dsh web service listens on. */
  port: number
}

/** One tray action the web UI can trigger. */
export type TrayAction = 'show' | 'restart' | 'exit'

/** Status snapshot the web UI card renders. */
export interface TrayStatus {
  /** Whether a DshTray.exe process is currently running. */
  trayRunning: boolean
  /** The resolved DshTray.exe path (null when not found). */
  trayPath: string | null
  /** The dsh service port being managed. */
  port: number
  /** Whether something is listening on the port. */
  serverRunning: boolean
  /** The listening process id (0 when none). */
  serverPid: number
}

/** Loopback-literal check plus browser same-origin markers. */
function isLoopbackRequest(request: IncomingMessage): boolean {
  const address = request.socket.remoteAddress
  if (address !== '127.0.0.1' && address !== '::1' && address !== '::ffff:127.0.0.1') return false
  const host = request.headers.host
  if (typeof host !== 'string') return false
  let hostUrl: URL
  try {
    hostUrl = new URL(`http://${host}`)
  } catch {
    return false
  }
  if (hostUrl.hostname !== '127.0.0.1' && hostUrl.hostname !== 'localhost' && hostUrl.hostname !== '[::1]') return false
  if (request.headers['sec-fetch-site'] === 'cross-site') return false
  const origin = request.headers.origin
  if (origin === undefined) return true
  try {
    return new URL(origin).host === hostUrl.host
  } catch {
    return false
  }
}

function writeJson(res: ServerResponse, status: number, body: unknown): void {
  const payload = JSON.stringify(body)
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'referrer-policy': 'no-referrer' })
  res.end(payload)
}

async function readJsonBody(req: IncomingMessage): Promise<Record<string, unknown> | undefined> {
  const chunks: Buffer[] = []
  let size = 0
  for await (const chunk of req) {
    const buffer = chunk as Buffer
    size += buffer.length
    if (size > 64 * 1024) return undefined
    chunks.push(buffer)
  }
  try {
    const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8'))
    return typeof parsed === 'object' && parsed !== null ? parsed as Record<string, unknown> : undefined
  } catch {
    return undefined
  }
}

/**
 * Resolve the DshTray.exe path: configured value, then DSH_TRAY_PATH, then
 * common install / dev-checkout locations. Returns null when not found.
 */
export function resolveTrayExe(cfg: Pick<TrayConfig, 'trayPath'>): string | null {
  const configured = cfg.trayPath.trim()
  if (configured !== '' && existsSync(configured)) return configured
  const fromEnv = process.env.DSH_TRAY_PATH
  if (fromEnv !== undefined && fromEnv !== '' && existsSync(fromEnv)) return fromEnv
  const candidates = [
    join(process.env.LOCALAPPDATA ?? '', 'dsh-tray', 'DshTray.exe'),
    join(process.env.ProgramFiles ?? '', 'dsh-tray', 'DshTray.exe'),
    // Dev checkout layout: C:\Users\<user>\project\Agent\dsh\dsh-tray\src\DshTray\bin\...
    join(homedir(), 'project', 'Agent', 'dsh', 'dsh-tray', 'src', 'DshTray', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'publish', 'DshTray.exe'),
  ]
  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate
  }
  return null
}

/** Spawn DshTray.exe detached and hidden; the tray / restart / exit keep running after this process dies. */
function spawnTray(exe: string, args: readonly string[]): { ok: true } | { ok: false; error: string } {
  try {
    const child = spawn(exe, [...args], {
      detached: true,
      stdio: 'ignore',
      windowsHide: true,
    })
    child.unref()
    return { ok: true }
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) }
  }
}

/** Whether a process with the given image name is currently running (tasklist). */
function imageRunning(imageName: string): Promise<boolean> {
  return new Promise((resolve) => {
    execFile('tasklist', ['/fi', `imagename eq ${imageName}`, '/fo', 'csv', '/nh'], { windowsHide: true }, (error, stdout) => {
      if (error) {
        resolve(false)
        return
      }
      resolve(stdout.toLowerCase().includes(imageName.toLowerCase()))
    })
  })
}

/** Find the pid listening on a TCP port (netstat parse). */
function findPidOnPort(port: number): Promise<number> {
  return new Promise((resolve) => {
    execFile('netstat', ['-ano', '-p', 'tcp'], { windowsHide: true }, (error, stdout) => {
      if (error) {
        resolve(0)
        return
      }
      const token = `:${port} `
      for (const line of stdout.split('\n')) {
        if (!line.includes('LISTENING')) continue
        if (!line.includes(token)) continue
        const parts = line.trim().split(/\s+/)
        const pid = Number(parts[parts.length - 1])
        if (Number.isFinite(pid) && pid > 0) {
          resolve(pid)
          return
        }
      }
      resolve(0)
    })
  })
}

/** Whether the port accepts TCP connections. */
function portOpen(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = createConnection({ host: '127.0.0.1', port })
    socket.setTimeout(1500, () => { socket.destroy(); resolve(false) })
    socket.once('connect', () => { socket.destroy(); resolve(true) })
    socket.once('error', () => resolve(false))
  })
}

/** Build the /api/dsh-tray route family. */
export function makeRoutes(getConfig: () => TrayConfig): { routes: WebRoute[] } {
  const routes: WebRoute[] = [
    {
      kind: 'exact',
      path: `${TRAY_API}/status`,
      handler: async (req, res) => {
        if (!isLoopbackRequest(req) || (req.method ?? 'GET') !== 'GET') {
          writeJson(res, isLoopbackRequest(req) ? 405 : 403, { error: 'forbidden or method not allowed' })
          return
        }
        const cfg = getConfig()
        const exe = resolveTrayExe(cfg)
        const trayRunning = exe !== null && await imageRunning(exe.split(/[\\/]/).pop() ?? 'DshTray.exe')
        const serverPid = await findPidOnPort(cfg.port)
        const status: TrayStatus = {
          trayRunning,
          trayPath: exe,
          port: cfg.port,
          serverRunning: serverPid > 0,
          serverPid,
        }
        writeJson(res, 200, { ok: true, status })
      },
    },
    {
      kind: 'exact',
      path: `${TRAY_API}/action`,
      handler: async (req, res) => {
        if (!isLoopbackRequest(req) || (req.method ?? 'GET') !== 'POST') {
          writeJson(res, isLoopbackRequest(req) ? 405 : 403, { error: 'forbidden or method not allowed' })
          return
        }
        const body = await readJsonBody(req)
        const action = typeof body?.action === 'string' ? body.action as TrayAction : undefined
        if (action !== 'show' && action !== 'restart' && action !== 'exit') {
          writeJson(res, 400, { error: `unknown action '${String(action)}'` })
          return
        }
        const cfg = getConfig()
        const exe = resolveTrayExe(cfg)
        if (exe === null) {
          writeJson(res, 409, { error: '未找到 DshTray.exe：请在设置中填写 trayPath，或安装 dsh-tray' })
          return
        }
        const args = action === 'show'
          ? []
          : action === 'restart'
            ? ['--restart', '--port', String(cfg.port)]
            : ['--stop', '--port', String(cfg.port)]
        const result = spawnTray(exe, args)
        if (!result.ok) {
          writeJson(res, 500, { error: result.error })
          return
        }
        // restart/exit terminate this very process shortly after; respond now.
        writeJson(res, 200, { ok: true, action, note: action === 'restart' ? 'restarting' : action === 'exit' ? 'exiting' : 'tray shown' })
      },
    },
  ]
  return { routes }
}
