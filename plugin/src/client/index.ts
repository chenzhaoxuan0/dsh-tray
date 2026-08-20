/**
 * dsh-tray-plugin browser half — runs inside the dsh web GUI. Registers the
 * `dsh-tray` locale dictionaries and seats the tray settings card into the
 * plugin configuration section's `settings.plugin.item` slot (the same seat
 * the official plugin cards render in), independent of any Web UI plugin
 * group.
 */

import type { ClientContext } from '@deepseek-ai/dsh-client-runtime/client'
// Type-only: pulls the locale plugin's Context merge (ctx.locale).
import type {} from '@deepseek-ai/dsh-client-locale/client'
// Type-only: pulls the settings-surface Context merge (ctx.settingsScope).
import type {} from '@deepseek-ai/dsh-client-ui-settings/client'
import type {} from '@deepseek-ai/dsh-client-ui-slots'
import { TrayCard, TrayCardController, type TrayCardFace, type TraySettings } from './TrayCard.tsx'
import { zh, en, type TrayKey } from './locales.ts'

export type { TrayCardProps, TrayStatus, TrayAction, TraySettings } from './TrayCard.tsx'
export type { TrayKey } from './locales.ts'

declare module '@deepseek-ai/dsh-client-ui-slots' {
  interface LocaleNamespaceMap {
    /** dsh-tray settings card copy. */
    'dsh-tray': TrayKey
  }

  interface SlotMap {
    /**
     * The plugin configuration section's card seat, declared at runtime by
     * the official settings-plugins package. Keyed by the settings namespace
     * the card edits; spelled here with the same shape so this package can
     * register its card without depending on that sibling UI package.
     */
    'settings.plugin.item': { kind: 'keyed'; scope: 'root'; owner: SettingsPluginItemOwnerProps }
  }
}

/** Owner share of a plugin card (the section supplies nothing). */
export interface SettingsPluginItemOwnerProps {
  /** Marker field: card owner props are intentionally empty. */
  children?: never
}

/** The settings namespace this card edits (the Host plugin registers it). */
const TRAY_SETTINGS_NS = 'dsh-tray'

/** Required services (settingsScope rides the connection transport + remote invalidation). */
export const inject = ['slots', 'locale', 'connection', 'settingsScope', 'remote']

/**
 * Register the tray settings card.
 * @param ctx - client root context (locale + slots services).
 */
export function apply(ctx: ClientContext): void {
  ctx.effect(() => ctx.locale.register(TRAY_SETTINGS_NS, { zh, en }), 'dsh-tray: dictionaries')

  const scope = ctx.settingsScope.bind<TraySettings>({ namespace: TRAY_SETTINGS_NS })
  const controller = new TrayCardController(scope)

  ctx.slots.inject('settings.plugin.item', () => ctx.slots.register({
    name: 'settings.plugin.item',
    key: TRAY_SETTINGS_NS,
    locale: TRAY_SETTINGS_NS,
    inject: (): TrayCardFace => controller.inject(),
  }, TrayCard))
}
