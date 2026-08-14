# dsh-tray

> DeepSeek Harness 的 Windows 系统托盘控制器：右键菜单只有两项 —— **重启** / **退出**。

![GitHub License](https://img.shields.io/badge/license-MIT-blue)

## 为什么不是插件？

开始前先看了官方插件开发文档 [develop/basic](https://deepseek-harness.github.io/deepseek-harness/develop/basic/)（本地对应 `deepseek-harness/docs/user/develop/basic/index.zh.md`）：

- 插件是**进程内**模块：导出 `apply(ctx)` 的 TS 文件，通过 `ctx` 注册工具（tools）、服务（services）、浏览器 UI（Slots）等能力；
- **没有任何原生系统托盘能力**——Node 进程没有托盘 API，DSH 也没有提供托盘服务；
- 托盘图标、右键菜单本质上是 Windows 原生外壳元素，必须有一个独立的原生进程来承载；就算做成插件，插件最终也还是要拉起一个外部托盘程序，而"重启/退出"还需要控制宿主进程本身，这在进程内部做非常别扭。

结论：**托盘不适合做成 Cordis 插件**，最自然的形态是独立的 Windows 小工具——与本机已有的 [dsh-launcher](https://github.com/Ruler4396/dsh-launcher)（C# WinForms / WebView2，.NET 10）同一技术栈。本仓库的 `ServerController` 逻辑（按端口找进程、识别、杀树、重放启动）将来若需要，可直接合并进 dsh-launcher 作为其托盘功能。

## 功能

- 🖥️ **系统托盘图标**：常驻通知区域，图标取自 dsh-launcher（DeepSeek 品牌图标，仅个人本地使用）
- 🖱️ **右键菜单恰好两项**：
  - **重启**：停止当前 dsh 服务 → 用**完全相同的命令行 + 工作目录**重新拉起（从正在运行的进程实况捕获，支持 `pnpm dsh web` / 源码 `node ... bin.ts web` / `npx @deepseek-ai/dsh web` 等任意启动方式）
  - **退出**：停止 dsh 服务并退出托盘
- 🖱️ **双击图标**：用默认浏览器打开 `http://127.0.0.1:3080`
- 🛡️ **防误杀**：只操作被识别为 dsh 的进程（端口有 HTTP 响应 **且** 命令行带 `dsh`/`harness`/`bin.ts`/`@deepseek-ai` 等特征）；端口被其他程序占用时拒绝操作并提示
- 📋 **日志**：`%USERPROFILE%\.dsh-tray.log`

## 构建

需要 [.NET SDK 10](https://dotnet.microsoft.com/)（本机已装 10.0.400；运行还需要 .NET Desktop Runtime 10，与 dsh-launcher 相同）。

```powershell
powershell -File scripts\build.ps1            # 框架依赖单文件（默认）
powershell -File scripts\build.ps1 -SelfContained  # 打包完整运行时，目标机免装 .NET
```

产物：`src\DshTray\bin\Release\net10.0-windows\win-x64\publish\DshTray.exe`

## 使用

```powershell
# 直接运行（单实例：重复启动自动忽略）
.\publish\DshTray.exe
```

首次运行会在 exe 同目录生成：

- `start-dsh.cmd` —— 兜底启动命令（仅当无法捕获正在运行进程的命令行时使用），可自行编辑；
- `tray.config.json` —— 可选配置：

```json
{
  "port": 3080,             // 服务端口
  "stopServerOnExit": true  // 托盘"退出"时是否同时停止 dsh 服务
}
```

想开机自启：`Win+R` → `shell:startup` → 把 `DshTray.exe` 的快捷方式放进去即可。

## 重启/退出到底做了什么

```
重启：netstat 找 3080 监听 PID → 校验是 dsh（HTTP 探测 + 命令行特征）
     → 捕获该进程的完整命令行 + 工作目录（PEB 读取）
     → taskkill 杀进程树 → 等端口释放
     → 用捕获的命令行在同样的工作目录重新启动（分离、无窗口）
     → 等端口就绪 + HTTP 探测通过 → 气泡提示结果
退出：同上的识别 → 停止 dsh 服务 → 退出托盘
```

注意：**重启后，当初手动启动 dsh 的那个终端会显示命令已结束**（原来的进程被杀掉了），新的服务是托盘拉起的独立进程，不依赖托盘存活。

## CLI 模式（测试 / 脚本化）

无托盘运行，便于验证与自动化：

```powershell
DshTray.exe --status            # 输出端口上的进程信息（pid / 是否 dsh / 命令行 / 工作目录）
DshTray.exe --restart           # 执行一次重启
DshTray.exe --stop              # 停止服务
DshTray.exe --test-capture      # 只读：验证命令行与工作目录捕获
DshTray.exe --port 3099 --status # 指定端口（默认 3080）
```

## 测试

仓库在 3099 端口用合成 HTTP 服务（命令行含 `harness` 特征）做了完整验证：

```
synthetic pid=11368 → --restart → pid=2148（新进程，同命令行同 cwd，HTTP 正常）
--stop → 端口释放，进程消失
```

对本机真实服务（3080，即本会话所在进程）只做了**只读**检查：`--status` / `--test-capture` 均正确识别出
`node --import tsx/esm apps/cli/src/bin.ts web` 与工作目录 `C:\Users\chenziyu\project\Agent\deepseek-harness\`。

## 目录结构

```
dsh-tray/
├── src/DshTray/
│   ├── DshTray.csproj      # net10.0-windows WinForms，单文件发布，内嵌图标
│   ├── Program.cs          # 入口：托盘（重启/退出菜单）+ CLI 模式
│   ├── ServerController.cs # 找进程/识别/杀树/重放启动/日志（与 UI 无关）
│   ├── NativeMethods.cs    # CommandLineToArgvW / PEB 读工作目录
│   └── assets/favicon.png  # 托盘图标（来自 dsh-launcher，MIT）
├── scripts/build.ps1
└── README.md
```

## 免责声明

独立第三方工具，与 DeepSeek / DeepSeek AI 官方无关。[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`）是官方项目（MIT）。图标使用 DeepSeek 品牌标识，版权归 DeepSeek 所有，仅作个人本地使用。

## 许可证

[MIT](LICENSE) © dsh-tray contributors
