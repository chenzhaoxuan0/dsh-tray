/**
 * tsdown build for dsh-tray-plugin:
 * - node half: lib/index.js + lib/routes.js (ESM, node), host process side;
 * - browser half: lib/client.js (CJS closure factory) registered with the
 *   package-name id `dsh-tray-plugin` (the module loader keys client modules
 *   on the package name — keep it in sync with package.json `name`).
 *
 * The client bundle replicates the official DSH client-bundle preset:
 * - externals resolve through the loader module table at runtime
 *   (the PLATFORM_MODULES seed list plus the runtime/client exemption);
 * - everything else is inlined (this bundle only value-imports react);
 * - the purity gate rejects any other @deepseek-ai value import
 *   (cross-plugin collaboration goes through cordis services);
 * - the artifact registers via window.__ModuleLoader__.load({id, factory})
 *   with the (require) => exports CJS closure shape.
 */
import { builtinModules } from 'node:module'
import type { UserConfig } from 'tsdown'

/** Node builtins must never survive into the browser module-loader factory. */
const NODE_BUILTINS = new Set([
  ...builtinModules,
  ...builtinModules.map(id => `node:${id}`),
])

/** Module specifiers the web shell shares into the frozen module table (the official PLATFORM_MODULES list, plus the runtime/client exemption). */
const CLIENT_EXTERNALS = [
  'react',
  'react/jsx-runtime',
  'react-dom',
  'react-dom/client',
  '@deepseek-ai/cordis',
  '@deepseek-ai/dsh-client-ui-slots',
  '@deepseek-ai/dsh-client-web-react',
  '@deepseek-ai/dsh-client-ui-primitives',
  '@deepseek-ai/dsh-client-schema-form',
  '@deepseek-ai/dsh-client-runtime/client',
]

/** Wire/type layers a client bundle may inline (mirror of the official INLINE_SAFE list). */
const INLINE_SAFE = /^@deepseek-ai\/dsh-(host-apiproxy|session|llm|tools|brand)(\/|$)/

/** The client bundle: one closure-factory script served at /plugins/dsh-tray-plugin/client.js. */
const clientConfig: UserConfig = {
  entry: { client: 'src/client/index.ts' },
  outDir: 'lib',
  format: 'cjs',
  platform: 'browser',
  dts: false,
  sourcemap: true,
  clean: false,
  external: [...CLIENT_EXTERNALS],
  define: {
    'process.env.NODE_ENV': JSON.stringify(process.env.NODE_ENV ?? 'production'),
    'import.meta.env.MODE': JSON.stringify(process.env.NODE_ENV ?? 'production'),
    'import.meta.env': JSON.stringify({ MODE: process.env.NODE_ENV ?? 'production' }),
  },
  // External wins for module-table entries; every other dependency inlines.
  noExternal: (id: string) => (CLIENT_EXTERNALS.includes(id) ? undefined : true),
  plugins: [{
    name: 'dsh-client-bundle-purity',
    resolveId(source: string) {
      if (NODE_BUILTINS.has(source)) {
        throw new Error(
          `client bundle purity: Node builtin "${source}" cannot run in the browser module table`,
        )
      }
      if (!source.startsWith('@deepseek-ai/')) return null
      if (CLIENT_EXTERNALS.includes(source)) return null
      if (INLINE_SAFE.test(source)) return null
      throw new Error(
        `client bundle purity: "${source}" is not a platform module (CLIENT_EXTERNALS) and not an inline-safe wire layer — `
        + 'cross-plugin value imports are forbidden; collaborate through cordis services',
      )
    },
  }],
  outputOptions: {
    entryFileNames: 'client.js',
    banner: `window.__ModuleLoader__.load({ id: ${JSON.stringify('dsh-tray-plugin')}, factory: (require) => {`,
    footer: 'return module.exports; } });',
    intro: 'var module = { exports: {} }; var exports = module.exports;',
    // The CJS wrapper's require only resolves module-table entries; keep one script.
    codeSplitting: false,
  },
}

/** The node-half library: runs inside the dsh host process. */
const libConfig: UserConfig = {
  entry: { index: 'src/index.ts', routes: 'src/routes.ts' },
  outDir: 'lib',
  format: ['esm'],
  platform: 'node',
  target: 'es2024',
  fixedExtension: false,
  dts: false,
  clean: false,
  // The cordis framework and the dsh-settings service provider resolve at
  // runtime from the dsh profile tree, never from this repo's install.
  external: ['@deepseek-ai/cordis', '@deepseek-ai/dsh-settings'],
}

export default [libConfig, clientConfig] satisfies UserConfig[]
