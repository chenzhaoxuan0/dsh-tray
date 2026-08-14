/**
 * dsh-tray-plugin — host half. Registers the `dsh-tray` settings namespace
 * (trayPath / port / enabled) and the loopback-only /api/dsh-tray route
 * family that drives the standalone DshTray.exe tray program:
 *   - 显示托盘图标 (show):   spawn DshTray.exe
 *   - 重启 (restart):        spawn `DshTray.exe --restart --port <port>`
 *                            (kills the dsh service tree and replays its
 *                            original command line — the very process this
 *                            plugin runs in dies and comes back)
 *   - 退出 (exit):           spawn `DshTray.exe --stop --port <port>`
 * The browser half (./client) renders the settings card with these actions.
 * Everything rides official NPM SDK packages — no dsh source changes.
 */

import type { Context } from '@deepseek-ai/cordis'
import { installSettingsSection, settingsNamespace } from '@deepseek-ai/dsh-settings'
import z from 'schemastery'
import type {} from '@deepseek-ai/dsh-host-webserver'
import { makeRoutes, type TrayConfig } from './routes.ts'

/** Stable cordis plugin name. */
export const name = 'dsh-tray'

/** Services required before the tray surfaces can mount. */
export const inject = ['webServer']

/** Settings namespace of the tray capability — the section the web settings surface edits. */
export const TRAY_SETTINGS_NAMESPACE = settingsNamespace('dsh-tray')

/** Plugin config, validated by the same-named schemastery schema. */
export interface Config {
  /** Master switch for the plugin (routes). */
  enabled?: boolean
  /** Absolute path to DshTray.exe; empty = auto-detect (env DSH_TRAY_PATH, known locations). */
  trayPath?: string
  /** Port the dsh web service listens on. */
  port?: number
}

export const Config: z<Config> = z.object({
  enabled: z.boolean().default(true),
  trayPath: z.string().default(''),
  port: z.number().default(3080),
})

/** Schema defaults, re-read for hand-built test contexts. */
const DEFAULT_ENABLED = true
const DEFAULT_TRAY_PATH = ''
const DEFAULT_PORT = 3080

/**
 * Mount the tray routes and settings section.
 * @param ctx - host plugin context carrying webServer.
 * @param config - resolved plugin config (schema defaults applied by the loader).
 */
export function apply(ctx: Context, config?: Config): void {
  let current: () => Config = () => config ?? {}
  const resolve = (): TrayConfig => ({
    enabled: current().enabled ?? DEFAULT_ENABLED,
    trayPath: current().trayPath ?? DEFAULT_TRAY_PATH,
    port: current().port ?? DEFAULT_PORT,
  })

  let disposeRoutes: (() => void) | undefined
  const sync = (): void => {
    if (disposeRoutes !== undefined) {
      disposeRoutes()
      disposeRoutes = undefined
    }
    if (!resolve().enabled) return
    const { routes } = makeRoutes(resolve)
    disposeRoutes = ctx.effect(
      () => {
        const disposers = routes.map(route => ctx.webServer.register(route))
        return () => { for (const dispose of disposers) dispose() }
      },
      'dsh-tray: routes',
    )
  }

  installSettingsSection(ctx, TRAY_SETTINGS_NAMESPACE, Config, config ?? {}, {
    setSource: (source) => {
      current = source
      sync()
    },
    onChange: sync,
  })

  // Initial registration from the composition entry (covers deployments with
  // no settings service, whose installSettingsSection never fires its hooks).
  sync()
}
