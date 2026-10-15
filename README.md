# 夜莺播放器 · Windows 0.1.2

> 共同工作区：[MediaSuite](../README.md) · 共同开发与前端规范：[AGENTS.md](../AGENTS.md)。

夜莺的 Windows 桌面版本。使用 C#、WPF、.NET 10 和 libmpv，面向 Windows 10/11 x64；直接打开本地音视频文件，提供字幕、播放恢复与睡眠定时。

应用名称为「夜莺播放器 · Nightingale」，采用月牙与青鸟图标，与媒体制作工具「织声 · Soundweaver」配套。图标母版、提示词与各入口资源见 [品牌资源](assets/README.md)。

`NightingaleDesktop` 是源码目录，`Nightingale.exe`、`Nightingale.*` 命名空间、`NIGHTINGALE_*` 环境变量、安装注册标识和 `%LOCALAPPDATA%\NightingalePlayer` 数据目录保留原值，兼容已有安装和设置。源码版本以 `src/Nightingale.Desktop/Nightingale.Desktop.csproj` 的 `Version` 为准；打包脚本据此命名产物，已有同版本 `dist` 目录不代表已包含最新源码修改。

## 打开与安装

本地打包产物位于：

- `dist/Nightingale-0.1.2-win-x64/Nightingale.exe`：自包含应用目录中的程序，运行时需要保留同目录的 DLL 和运行时文件。
- `dist/Nightingale-0.1.2-setup.exe`：安装器，按当前用户安装到 `%LOCALAPPDATA%\Programs\Nightingale`。

安装后可从开始菜单打开“夜莺播放器”。安装向导提供可选桌面快捷方式。自包含版本包含 .NET 运行时，使用者无需另装开发 SDK。

在主界面选择“打开文件”或“打开文件夹”，也可以把媒体文件、文件夹拖入窗口。文件夹会递归扫描可支持的格式，并按自然文件名顺序组成播放列表；播放列表可收起。再次打开媒体文件会转交给同一用户的现有夜莺窗口。

把文件拖入左侧播放列表会追加到队列，并保留当前播放的文件、进度和暂停状态；重复文件不会重复添加。把文件拖入播放画面会打开新队列；按住 `Ctrl` 拖入画面则追加。单个字幕文件始终用于给当前文件加载字幕。

## 设为默认播放器

1. 运行安装器，完成夜莺的应用与文件格式登记。
2. 在播放器的设置菜单中选择“设为默认播放器…”。
3. 在 Windows“默认应用”中找到夜莺，为 `.mp4`、`.mkv` 等需要的格式选择夜莺。Windows 10 或部分 Windows 11 版本可能进入默认应用总页面，可按扩展名查找。
4. 此后双击关联格式的文件即可启动夜莺播放。

安装器登记“打开方式”与默认应用候选项，最终默认选择由用户在 Windows 中完成。直接运行应用目录里的程序不会执行安装登记。卸载会移除本应用注册及安装文件，保留播放记录和个人设置。

## 当前功能

- 本地视频与音频播放，打开多个文件、添加到队列、目录扫描、持久保存最近播放和播放进度；可开启无痕播放。
- 进度条点击与拖动定位、0.5× 至 3× 的常用倍速、按住右方向键临时 3× 播放、音量、静音、原声／+6 dB／+12 dB 增益。
- 播放列表顺序播放和循环；音频文件使用简洁的歌词界面，没有歌词时保持留白。
- 窗口置顶、全屏、全屏时自动隐藏控制条，画面适应窗口、填满裁剪和拉伸模式。
- 同目录字幕自动匹配，手动加载字幕，切换内嵌字幕与音轨，调整字幕字号、底部距离和延迟。
- 外挂 LRC、VTT、SRT 的上一句／下一句跳转，可选点击或拖动进度、方向键跳转时吸附到字幕起点。ASS、SSA 和内嵌字幕由播放内核显示；这些字幕当前不提供应用层的按句导航。
- 15／30／60／90 分钟或自定义 1–1440 分钟的睡眠定时；到时暂停播放。定时属于当前会话，关闭程序后不保留。
- 视频实际播放时阻止显示器和系统自动休眠；纯音频播放时允许熄屏、保持系统唤醒。暂停、播放结束、播放错误或退出时恢复。

当前尚未实现桌面悬浮歌词、系统媒体键、任务栏播放按钮、托盘或迷你窗口。

## 文件与字幕格式

| 类型 | 当前接受的扩展名 |
| --- | --- |
| 视频 | `.mp4`、`.mkv`、`.mov`、`.webm`、`.avi`、`.m4v`、`.ts` |
| 音频 | `.mp3`、`.wav`、`.flac`、`.m4a`、`.aac`、`.ogg`、`.opus` |
| 外挂字幕／歌词 | `.lrc`、`.vtt`、`.srt`、`.ass`、`.ssa` |

媒体扫描、打开文件筛选器和安装器默认应用登记使用相同的 14 种媒体扩展名。具体文件能否解码取决于其中的编码和文件完整性。

例如播放 `夜雨.mp4` 时，会查找同目录中的 `夜雨.srt`、`夜雨.vtt`、`夜雨.zh-CN.ass`、`夜雨.mp4.lrc` 等同名前缀字幕；优先匹配完全同名的字幕。单独拖入一个字幕文件或使用“字幕 → 加载外挂字幕…”可以手动加载。

在底部的“字幕”菜单中勾选“进度定位吸附到字幕起点”，可让进度条点击和拖动落在字幕句子的起点；短按 `←` / `→` 则跳到上一句／下一句。`Shift+←` / `Shift+→` 仍以 30 秒为步长，并将目标吸附到字幕起点。取消勾选后，进度条按所选时间精确定位，短按左右方向键后退／前进 5 秒。该开关会自动保存，字幕设置中也可调整。吸附仅适用于已加载、提供时间轴的外挂 VTT、SRT、LRC，并考虑设置的字幕延迟；没有可用时间轴时方向键按秒数跳转。`[` / `]` 的上一句／下一句导航可独立使用，无需开启进度吸附。

播放恢复会跳过小于等于 3 秒的位置和接近结尾的记录。按 `Home` 可从头播放。最近播放最多保存 100 个文件，并保存普通模式下的播放队列；重新打开程序可从“最近播放”或“当前队列”继续选择，启动时不会自动播放。

在“设置 → 更多设置与快捷键”的“隐私与播放记录”中开启“无痕播放”后，新打开的文件不读取旧的续播位置，也不保存新的最近播放、播放进度或恢复队列；原有历史记录会保留，并在无痕期间隐藏。设置菜单中也可直接切换无痕播放。无痕开关会跨重启保存。关闭无痕后，之后打开的文件恢复正常记录；当前已经处于无痕播放的文件仍保持无痕，避免退出时补记。无痕模式只管理夜莺自己的播放记录，不会清除 Windows 文件资源管理器等外部程序的访问记录。

## 快捷键

| 操作 | 按键 |
| --- | --- |
| 打开文件 | `Ctrl+O` |
| 打开文件夹 | `Ctrl+Shift+O` |
| 播放／暂停 | `Space`；视频或音频画面单击 |
| 全屏／退出全屏 | `F`、`F11`；视频画面双击 |
| 退出全屏 | `Esc` |
| 后退／前进 5 秒；开启字幕吸附时上一句／下一句 | 短按 `←` / `→` |
| 临时 3× 播放 | 按住 `→` 约 0.3 秒，松开恢复之前选择的倍速 |
| 后退／前进 30 秒，遵循字幕吸附设置 | `Shift+←` / `Shift+→` |
| 上一个／下一个文件 | `Ctrl+←` / `Ctrl+→` |
| 音量加／减 | `↑` / `↓`；视频画面滚轮 |
| 静音 | `M` |
| 从头播放 | `Home` |
| 上一句／下一句 | `[` / `]` |
| 显示／收起播放列表 | `L` |

右方向键短按用于跳转，持续按住约 0.3 秒才进入临时倍速，长按后的松开不会再额外跳转。临时倍速不会修改已保存的倍速；暂停时不会因为按住或松开右方向键而开始播放。播放器失去焦点或切换文件时会恢复所选倍速。

## 构建与运行

需要 Windows、.NET 10 SDK，以及脚本获取的 x64 libmpv。构建脚本优先使用 `D:\tools\dotnet\dotnet.exe`，找不到时使用 `PATH` 中的 `dotnet`。

在项目目录的 PowerShell 中执行。已有 `native/mpv-2.dll` 时可直接构建；仅缺少原生库时运行获取脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Get-Mpv.ps1
.\Build.cmd -Configuration Debug
```

`Get-Mpv.ps1` 将固定版本的下载缓存保存在 `D:\tools\mpv`，校验 SHA-256 后解压并复制到 `native/mpv-2.dll`，可能替换已有 DLL。缓存已存在时可加 `-SkipDownload`；此选项仍会解压和复制文件。依赖来源与许可证说明见 [native/THIRD_PARTY.md](native/THIRD_PARTY.md)。

`Build.cmd` 默认只构建；加上 `-Run` 才会打开真实播放器窗口，使用默认数据目录并参与单实例转交。日常运行可执行 `.\Build.cmd -Configuration Debug -Run`。首次构建/测试可能还原缺失的 .NET 依赖，构建命令不是只读检查。

构建结果位于 `tmp/build/Nightingale.Desktop/bin/Debug/net10.0-windows/`；Release 构建对应 `Release` 目录。中间文件统一放在 `tmp/build`，不会散落在源代码目录。

运行核心自动检查：

```powershell
& 'D:\tools\dotnet\dotnet.exe' run --project .\tests\Nightingale.Core.Tests\Nightingale.Core.Tests.csproj --configuration Release
```

核心检查不创建播放器窗口，测试数据保留在 `tmp/core-tests/<时间戳与随机值>`。三个测试项目都是可执行检查程序，使用 `dotnet run`；不要用 `dotnet test` 的成功退出代替这些检查。

控件检查入口及限制见 [控件检查说明](tests/Nightingale.Controls.Tests/README.md)：

```powershell
& 'D:\tools\dotnet\dotnet.exe' run --project .\tests\Nightingale.Controls.Tests\Nightingale.Controls.Tests.csproj --configuration Release
```

默认控件检查创建屏幕外的 WPF 测试窗口，不播放媒体、不移动鼠标；追加 `-- --pointer` 会显示测试窗口并移动系统鼠标，需要预留无人操作鼠标的时段。

运行播放集成检查（会创建独立的隐藏播放窗口和 `tmp/integration-tests` 测试素材，不复用日常播放器的状态）：

```powershell
& 'D:\tools\dotnet\dotnet.exe' run --project .\tests\Nightingale.Integration.Tests\Nightingale.Integration.Tests.csproj --configuration Release -- --skip-fullscreen
```

生成素材需要 FFmpeg，默认使用 `D:\tools\ffmpeg\ffmpeg-9.0.1-essentials_build\bin\ffmpeg.exe`，也可通过 `NIGHTINGALE_FFMPEG` 指定路径。省略 `--skip-fullscreen` 会额外检查全屏和退出全屏；该检查会暂时显示测试窗口。

集成检查使用真实 `MainWindow` 和 libmpv，生成媒体、隔离状态与日志；即使加 `--skip-fullscreen` 也会创建测试窗口和播放媒体。详细覆盖范围见 [播放集成检查说明](tests/Nightingale.Integration.Tests/README.md)。这些检查不等于安装注册、真实输入、显示缩放与视觉外观已通过验收。

生成本地自包含应用和安装器：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1 -Installer
```

`-Installer` 需要 Inno Setup，默认编译器路径为 `D:\tools\inno-setup\compiler\ISCC.exe`，也支持 `PATH` 中的 `ISCC.exe` 或显式 `-InnoCompiler`。两条命令是不同打包选项，生成安装器只需执行第二条；它也会先生成自包含目录。重复打包会更新同版本 `dist` 中的编译产物与说明文件，不清空该目录。打包不会自动安装播放器或发布到远程；原生组件声明随包复制到 `ThirdPartyNotices/mpv`。公开分发前需要按 `native/THIRD_PARTY.md` 中的说明补齐对应源代码和完整组件许可材料。

## 设置与诊断

默认数据目录是 `%LOCALAPPDATA%\NightingalePlayer`：

- `state.json`：音量、倍速、增益、字幕、置顶、循环、无痕偏好，以及最近播放记录和普通播放队列。
- `state.json.bak`：上次成功保存的备份；主文件无法读取时尝试恢复。
- `error.log`：界面记录的异常诊断信息，发生相应错误时创建。

记录会定期保存并在退出时保存。独立测试时可设置 `NIGHTINGALE_DATA_DIR` 为项目 `tmp` 内的绝对目录；它同时隔离状态文件和单实例通信，避免测试抢占日常播放器窗口。

```powershell
$env:NIGHTINGALE_DATA_DIR = Join-Path (Get-Location) 'tmp\manual-session'
.\Build.cmd -Run
```

核心逻辑位于 `src/Nightingale.Core`，WPF 界面和 Windows 集成位于 `src/Nightingale.Desktop`，libmpv 接口位于 `src/Nightingale.Desktop/Playback`。
