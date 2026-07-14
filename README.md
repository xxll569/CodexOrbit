# Codex Orbit

[简体中文](README.md) | [English](README_EN.md)

轻量 Windows 用量悬浮窗，以紧凑圆环展示 Codex 的 5 小时和周额度。

## 预览

<p align="center">
  <a href="image/preview-showcase-transparent.png"><img src="image/preview-showcase-transparent.png" alt="Codex Orbit Mini 模式预览" width="600" /></a>
</p>

## 快速开始

1. 从 [Releases](https://github.com/xxll569/CodexOrbit/releases/latest) 下载最新便携包。
2. 解压后直接运行 `CodexOrbit.exe`。支持 Windows 10/11，系统通常已内置所需运行环境，无需额外安装。
3. 正常使用 Codex，额度快照写入本地日志后圆环会自动刷新。

- 鼠标悬浮：查看同步状态、重置时间和最后快照。
- 拖动圆周：调整悬浮窗大小。
- 程序启动后直接显示 Mini 仪表胶囊，不再提供其他显示模式。
- 拖动 Mini：自由调整位置，程序会自动记住最后位置。
- 将 Mini 靠近屏幕左右边缘：自动吸附并收成细长把手，悬停即可展开。
- 右键 Mini：重新读取、切换置顶或退出。

周额度显示在圆形仪表中；如果本地日志包含有效的 5 小时额度，左侧胶囊会自动显示额度和重置倒计时，否则只显示周额度圆环。

程序仅读取 `%USERPROFILE%\.codex\sessions` 中的本地额度日志，不读取账号令牌。
### 如果 Codex Orbit 帮到了你，欢迎留下一个 ⭐ Star，让更多开发者发现它。

### 社区：[LINUX DO — 中文开发者社区](https://linux.do/)