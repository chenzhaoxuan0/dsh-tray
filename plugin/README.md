# dsh-tray-plugin

DeepSeek Harness Web 设置插件：**显示托盘图标 / 重启 / 退出**，驱动同仓库的托盘程序
（`../src/DshTray` 构建出的 `DshTray.exe`）。本目录是合并仓库 `dsh-tray` 的 `plugin/` 组件，
完整说明见仓库根目录 [../README.md](../README.md)。

- 挂载：profile bundle 机制（`dsh.bundle.patch` = `./cordis.patch.yml`），不修改 DSH / dsh-web-ui 源码；
- 设置卡片注册到官方 `settings.plugin.item` 槽位（设置 -> 插件 页面）；
- host 半区：`dsh-tray` 设置命名空间 + 回环隔离的 `/api/dsh-tray/status`、`/api/dsh-tray/action`；
- 托盘程序路径解析：设置项 `trayPath` -> 环境变量 `DSH_TRAY_PATH` -> `%LOCALAPPDATA%\dsh-tray\DshTray.exe` -> 常见位置。

```sh
pnpm install && pnpm build   # 产物: lib/{index,routes,client}.js
```
