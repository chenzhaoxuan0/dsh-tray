/**
 * The dsh-tray settings card, registered into the plugin configuration
 * section's `settings.plugin.item` seat (the same slot the official plugin
 * cards and the dsh-web-ui family group card use). Shows live tray/service
 * status and the three actions: 显示托盘图标 / 重启 / 退出, plus a small
 * form for the DshTray.exe path and service port.
 */

import { createElement, useEffect, useRef, useState } from 'react'
import type { InjectFace, PropsLocale, PropsRuntime } from '@deepseek-ai/dsh-client-ui-slots'
import type { SettingsScope, SettingsScopeSnapshot } from '@deepseek-ai/dsh-client-runtime/client'
import type { TrayKey } from './locales.ts'

/** The plugin settings the card edits (mirror of the host Config schema). */
export interface TraySettings {
  enabled?: boolean
  trayPath?: string
  port?: number
}

/** Status snapshot returned by the host /api/dsh-tray/status route. */
export interface TrayStatus {
  trayRunning: boolean
  trayPath: string | null
  port: number
  serverRunning: boolean
  serverPid: number
}

/** One action the card can trigger on the host. */
export type TrayAction = 'show' | 'restart' | 'exit'

/** The registration-side face the card's slot entry injects. */
export interface TrayCardFace {
  /** Fetch the current tray/service status from the host. */
  status: () => Promise<TrayStatus>
  /** Trigger one host action. */
  run: (action: TrayAction) => Promise<{ ok: boolean; error?: string }>
  /** Read the current effective settings snapshot. */
  snapshot: () => SettingsScopeSnapshot<TraySettings>
  /** Write one settings field (trayPath / port). */
  setField: (field: 'trayPath' | 'port', value: string) => Promise<void>
}

/** Bridges the `dsh-tray` settings scope and the host routes onto the card. */
export class TrayCardController {
  private readonly scope: SettingsScope<TraySettings>

  /** @param scope - the bound settings scope for the `dsh-tray` namespace. */
  constructor(scope: SettingsScope<TraySettings>) {
    this.scope = scope
  }

  private static async fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(path, init)
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { error?: string } | null
      throw new Error(body?.error ?? `http ${response.status}`)
    }
    return (await response.json()) as T
  }

  /** Build the face the card's slot registration injects. */
  inject(): TrayCardFace {
    return {
      status: async () => {
        const payload = await TrayCardController.fetchJson<{ ok: true; status: TrayStatus }>('/api/dsh-tray/status')
        return payload.status
      },
      run: async (action) => {
        try {
          const payload = await TrayCardController.fetchJson<{ ok: true }>('/api/dsh-tray/action', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ action }),
          })
          return { ok: payload.ok }
        } catch (error) {
          return { ok: false, error: error instanceof Error ? error.message : String(error) }
        }
      },
      snapshot: () => this.scope.getSnapshot(),
      setField: async (field, value) => {
        if (field === 'port') {
          const parsed = Number(value)
          if (!Number.isFinite(parsed) || parsed <= 0 || parsed > 65535) {
            throw new Error('port must be a number between 1 and 65535')
          }
          await this.scope.set('port', parsed)
          return
        }
        const trimmed = value.trim()
        if (trimmed === '') await this.scope.unset('trayPath')
        else await this.scope.set('trayPath', trimmed)
      },
    }
  }
}

/** Props the renderer binds for the tray settings card. */
export type TrayCardProps =
  PropsRuntime<'settings.plugin.item'>
  & PropsLocale<'dsh-tray'>
  & InjectFace<TrayCardFace>

/** Card shell: mirrors the standard plugin card (ui-plugin-config PluginCard). */
const cardStyle: React.CSSProperties = {
  listStyle: 'none',
  border: '1px solid var(--dsw-alias-border-l2)',
  borderRadius: '12px',
  background: 'var(--dsw-alias-bg-layer-3)',
  margin: '8px 0',
  transition: 'border-color .16s, background .16s',
}

/** Open card reads as the one being worked on (matches PluginCard.cardOpen). */
const cardOpenStyle: React.CSSProperties = {
  ...cardStyle,
  background: 'var(--dsw-alias-bg-layer-2)',
  borderColor: 'var(--dsw-alias-label-dimmed)',
}

/** Card header button (matches PluginCard.header). */
const headerStyle: React.CSSProperties = {
  width: '100%',
  appearance: 'none',
  border: 0,
  background: 'none',
  font: 'inherit',
  color: 'inherit',
  textAlign: 'left',
  cursor: 'pointer',
  display: 'flex',
  alignItems: 'center',
  gap: '12px',
  padding: '14px 16px',
  borderRadius: '12px',
}

/** Name + description column (matches PluginCard.headText). */
const headTextStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
  display: 'flex',
  flexDirection: 'column',
  gap: '4px',
}

/** Plugin name (matches PluginCard.name). */
const nameStyle: React.CSSProperties = {
  fontSize: '15px',
  fontWeight: 600,
  lineHeight: 1.4,
  color: 'var(--dsw-alias-label-primary)',
}

/** Description / hints (matches PluginCard.description). */
const descStyle: React.CSSProperties = {
  fontSize: '13px',
  lineHeight: 1.5,
  color: 'var(--dsw-alias-label-tertiary)',
}

/** Chevron (matches PluginCard.chevron). */
const chevronStyle: React.CSSProperties = {
  flex: 'none',
  color: 'var(--dsw-alias-label-tertiary)',
}

/** Minimal row for the card body. */
const rowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '8px',
  flexWrap: 'wrap',
  margin: '8px 0',
}

/** Secondary button (matches PluginCard.discard). */
const buttonStyle: React.CSSProperties = {
  padding: '5px 14px',
  borderRadius: '8px',
  border: '1px solid var(--dsw-alias-border-l2)',
  background: 'none',
  color: 'var(--dsw-alias-label-secondary)',
  cursor: 'pointer',
  fontSize: '13px',
  lineHeight: 1.5,
}

/**
 * Danger-tinted action (restart / exit): solid error fill with the layer
 * color as text — the design system's canonical danger treatment (same as
 * AgentPresetSection.brokenBadge). Do NOT use `state-error-secondary` as the
 * background here: in dark mode it resolves to the SAME color as
 * `state-error-primary`, which would make the text invisible (red on red).
 */
const dangerButtonStyle: React.CSSProperties = {
  ...buttonStyle,
  borderColor: 'var(--dsw-alias-state-error-primary)',
  background: 'var(--dsw-alias-state-error-primary)',
  color: 'var(--dsw-alias-bg-layer-3)',
}

/** Form field (themed input). */
const fieldStyle: React.CSSProperties = {
  display: 'block',
  width: '100%',
  boxSizing: 'border-box',
  padding: '6px 10px',
  borderRadius: '8px',
  border: '1px solid var(--dsw-alias-border-l2)',
  background: 'var(--dsw-alias-bg-layer-1)',
  color: 'inherit',
  fontSize: '13px',
  marginTop: '4px',
}

/** Primary button (matches PluginCard.save). */
const primaryButtonStyle: React.CSSProperties = {
  ...buttonStyle,
  background: 'var(--dsw-alias-label-primary)',
  color: 'var(--dsw-alias-bg-layer-3)',
}

/**
 * Render the tray settings card.
 * @param props - locale copy and the injected tray face.
 * @returns the card, or nothing while the namespace is still loading.
 */
export function TrayCard(props: TrayCardProps) {
  const { t } = props
  const [open, setOpen] = useState(false)
  const [status, setStatus] = useState<TrayStatus | null>(null)
  const [busy, setBusy] = useState<TrayAction | 'saving' | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [trayPath, setTrayPath] = useState('')
  const [port, setPort] = useState('3080')
  const [saved, setSaved] = useState(false)
  const seeded = useRef(false)

  const refresh = async (): Promise<void> => {
    try {
      const next = await props.status()
      setStatus(next)
      setError(null)
      if (!seeded.current) {
        seeded.current = true
        const snapshot = props.snapshot()
        const value = snapshot.status === 'ready' ? snapshot.value : undefined
        setTrayPath(value?.trayPath ?? '')
        setPort(String(value?.port ?? next.port))
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t('error.transport'))
    }
  }

  useEffect(() => { void refresh() }, [])

  const run = async (action: TrayAction): Promise<void> => {
    if (action === 'restart' && !window.confirm(t('action.restartConfirm'))) return
    if (action === 'exit' && !window.confirm(t('action.exitConfirm'))) return
    setBusy(action)
    setNotice(null)
    setError(null)
    const result = await props.run(action)
    setBusy(null)
    if (!result.ok) {
      setError(result.error ?? t('error.transport'))
      return
    }
    if (action === 'restart') setNotice(t('action.restarting'))
    else if (action === 'exit') setNotice(t('action.exiting'))
    else {
      setNotice(null)
      void refresh()
    }
  }

  const save = async (): Promise<void> => {
    setBusy('saving')
    setSaved(false)
    setError(null)
    try {
      await props.setField('trayPath', trayPath)
      await props.setField('port', port)
      setSaved(true)
      void refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t('form.saveFailed'))
    } finally {
      setBusy(null)
    }
  }

  const snapshotStatus = props.snapshot().status
  if (snapshotStatus === 'loading') return null

  const trayRunning = status?.trayRunning ?? false
  const serverRunning = status?.serverRunning ?? false
  const noExe = status !== null && status.trayPath === null

  return (
    <li style={open ? cardOpenStyle : cardStyle}>
      <button
        type="button"
        style={headerStyle}
        aria-expanded={open}
        onClick={() => { setOpen(!open) }}
      >
        <span style={headTextStyle}>
          <span style={nameStyle}>{t('settings.title')}</span>
          <span style={descStyle}>{t('settings.description')}</span>
        </span>
        <span style={chevronStyle}>{open ? '▾' : '▸'}</span>
      </button>

      {open
        ? (
          <div style={{ borderTop: '1px solid var(--dsw-alias-border-l2)', margin: '0 16px', paddingBottom: '8px' }}>
            {/* ---- status ---- */}
            <div style={rowStyle}>
              <span style={{ fontSize: '13px' }}>{trayRunning ? t('status.trayRunning') : t('status.trayStopped')}</span>
              <span style={{ opacity: 0.5 }}>·</span>
              <span style={{ fontSize: '13px' }}>
                {serverRunning && status !== null
                  ? `${t('status.serverRunning')} (pid=${status.serverPid})`
                  : t('status.serverStopped')}
              </span>
            </div>
            {status !== null
              ? (
                <p style={descStyle}>
                  {t('status.trayPath')}: {status.trayPath ?? '—'}
                </p>
              )
              : null}
            {noExe ? <p style={{ ...descStyle, color: 'var(--dsw-alias-state-warn-primary)' }}>{t('action.noTrayExe')}</p> : null}
            {error !== null ? <p style={{ ...descStyle, color: 'var(--dsw-alias-state-error-primary)' }} role="status">{error}</p> : null}
            {notice !== null ? <p style={{ ...descStyle, color: 'var(--dsw-alias-state-success-primary)' }} role="status">{notice}</p> : null}

            {/* ---- actions ---- */}
            <div style={rowStyle}>
              <button
                type="button"
                style={buttonStyle}
                disabled={busy !== null || trayRunning}
                onClick={() => { void run('show') }}
              >
                {busy === 'show' ? t('action.busy') : t('action.showTray')}
              </button>
              <button
                type="button"
                style={dangerButtonStyle}
                disabled={busy !== null}
                onClick={() => { void run('restart') }}
              >
                {busy === 'restart' ? t('action.busy') : t('action.restart')}
              </button>
              <button
                type="button"
                style={dangerButtonStyle}
                disabled={busy !== null}
                onClick={() => { void run('exit') }}
              >
                {busy === 'exit' ? t('action.busy') : t('action.exit')}
              </button>
            </div>

            {/* ---- config form ---- */}
            <div style={{ marginTop: '10px' }}>
              <label style={{ fontSize: '13px', display: 'block' }} htmlFor="dsh-tray-path">
                {t('form.trayPath')}
              </label>
              <input
                id="dsh-tray-path"
                style={fieldStyle}
                type="text"
                value={trayPath}
                placeholder="C:\...\DshTray.exe"
                onChange={(event) => { setTrayPath(event.target.value); setSaved(false) }}
              />
              <p style={descStyle}>{t('form.trayPathHint')}</p>

              <label style={{ fontSize: '13px', display: 'block', marginTop: '10px' }} htmlFor="dsh-tray-port">
                {t('form.port')}
              </label>
              <input
                id="dsh-tray-port"
                style={{ ...fieldStyle, width: '120px' }}
                type="text"
                inputMode="numeric"
                value={port}
                onChange={(event) => { setPort(event.target.value); setSaved(false) }}
              />
              <p style={descStyle}>{t('form.portHint')}</p>

              <div style={rowStyle}>
                <button
                  type="button"
                  style={primaryButtonStyle}
                  disabled={busy !== null}
                  onClick={() => { void save() }}
                >
                  {busy === 'saving' ? t('form.saving') : saved ? t('form.saved') : t('form.save')}
                </button>
              </div>
            </div>
          </div>
        )
        : null}
    </li>
  )
}
