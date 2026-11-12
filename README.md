# dsh-screenshot-hotkey

微信式截图标注，直接插进 DeepSeek Harness 的输入框。**系统级热键，按下时不会把 DSH 抢到前台**——框选的就是你当前正在看的那个窗口。

WeChat-style screenshot + annotation for the DSH composer, with a **system-wide hotkey that never steals focus** from the window you are snipping.

> 基于 [@sfunction/dsh-screenshot](https://github.com/sfunction-guo/dsh-screenshot)（MIT）扩展，保留原版权声明，见 [LICENSE](LICENSE)。

## 功能 / Features

- **Ctrl+Alt+S 随处可用**：DSH 在前台、在后台、最小化都行；不会弹窗遮住要截的区域。
- **框选 + 标注**：冻结屏幕、拖拽选区、画笔 / 矩形 / 文字 / 马赛克 / 颜色，Enter 或 ✓ 完成。
- **右键取消**（Esc 也可，且逐级返回：先撤标注 → 再清选区 → 最后关窗）。
- **自动插入**：截完直接贴进当前会话输入框；找不到输入框时退回剪贴板并提示。
- **可在面板改键**：DSH「设置 → 通用设置 → 快捷键」里搜「截图标注」；改完全局热键跟着变。
- **自带设置页**：DSH「设置 → 截图标注」显示当前键位。

## 安装 / Install

```sh
dsh plugin --profile desktop add github:gcolt888/dsh-screenshot-hotkey
```

或从市场安装（收录后）。装完重启 DSH。

## 要求 / Requirements

- **Windows**（WPF/WinForms 原生选区窗口）。
- .NET Framework 4 的 `csc.exe`（Windows 自带）。首次截图 / 首次启动时会从 `select.cs`、`hotkey.cs` 现场编译 `shot.exe`、`hotkey.exe`。

## 自定义 / Customize

- **改键**：DSH 设置 → 通用设置 → 快捷键 → 搜「截图标注」→ 录制新键位。全局热键会自动重载（读 `keybindings.json`）。
- **改截图入口**：编辑 `select.cs` 后重编译 `shot.exe`：

```powershell
csc -nologo -target:winexe -optimize -out:shot.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll select.cs
```

## 工作方式 / How it works

| 部分 | 作用 |
|---|---|
| `client.js` | 输入框左侧按钮；向 DSH 快捷键服务登记 `dsh-screenshot.capture`（只占位、可改键）；长轮询 `/shot/pending` 取外部截图并贴进输入框 |
| `index.js` | 宿主半：`/shot/capture` 打开选区、`/shot/pending` 长轮询交付、拉起并守护 `hotkey.exe` |
| `hotkey.cs` → `hotkey.exe` | 系统级 `RegisterHotKey`，按下直接跑 `shot.exe`（**不激活 DSH**），结果写进临时队列目录；读 `keybindings.json` 跟随用户改键 |
| `select.cs` → `shot.exe` | 全屏冻结 + 框选 + 标注的原生窗口 |

日志：`%TEMP%\dsh-shot-hotkey.log`。

## exe 运行在哪里（v0.4.4 起）

**不在插件目录里**，而是启动时复制到 `%LOCALAPPDATA%\dsh-screenshot-hotkey\bin\<版本指纹>\` 再运行。

原因：Windows 不允许替换「正在运行的 exe 所在目录」，而 `hotkey.exe` 是常驻的。
如果它跑在 `node_modules\dsh-screenshot-hotkey\` 里，**你之后安装或更新任何其他插件都会失败**
（pnpm 要整目录替换它 → `ERR_PNPM_EPERM`），更糟的是 pnpm 先删后建，每失败一次都会从插件目录里
删掉几个文件（`client.js`、`cordis.patch.yml` 丢了就等于插件静默失效）。

放到包外以后，插件目录是纯文件，pnpm 随时可以换。目录名带源文件指纹，新版会落到新目录，
所以永远不会覆盖一个正在运行的 exe；旧目录在下次启动时清理。

> The exes run from `%LOCALAPPDATA%\dsh-screenshot-hotkey\bin\` on purpose: a running exe pins its
> package directory on Windows, which makes `pnpm add` fail with `ERR_PNPM_EPERM` for *any* plugin the
> user installs afterwards. Keeping them outside the package keeps the package replaceable.

## 已知限制 / Known limits

- 仅 Windows。
- 若热键与其他软件冲突（如 QQ 截图默认 Ctrl+Alt+A），在面板里改键即可。

## 开发 / Development

改完源码后同步到本机 DSH profile（DSH 从 profile 的 `node_modules` 按包名加载，**必须是真实目录，不能用 junction**——实测 junction 会让客户端启动无限卡在 Loading plugins）：

```bat
sync-to-profile.cmd
```

然后重启 DSH。日志：`%TEMP%\dsh-shot-hotkey.log`。

## License

MIT — 见 [LICENSE](LICENSE)。原始版本 © 2026 sfunction-guo，本仓库的改动 © 2026 gcolt888。
