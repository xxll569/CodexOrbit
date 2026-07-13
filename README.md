# Codex Orbit

轻量 Windows WPF 用量悬浮窗，以紧凑圆环展示 Codex 的 5 小时和周额度快照。

## 功能

- 紫色外环显示周额度，蓝色内环显示 5 小时额度。
- 缺少有效的 5 小时额度时自动切换为单环布局。
- 鼠标悬浮显示同步状态、重置倒计时和最后快照时间。
- 支持圆形边缘缩放、始终置顶、托盘菜单和单实例运行。
- 窗口外和圆环间使用透明背景，适合作为桌面常驻小工具。

## 构建

开发机器建议安装 .NET Framework 4.8 Developer Pack，以获得完整的引用程序集和无警告构建；最终用户不需要安装开发包。

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe' .\CodexQuota.sln /p:Configuration=Release /m
```

生成文件位于 `src\CodexQuota\bin\Release\CodexOrbit.exe`。

也可以直接运行仓库 `dist` 目录中的便携版本 `CodexOrbit.exe`。

## 数据与隐私

- 仅读取 `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`。
- 不读取 `auth.json`、Cookie 或访问令牌。
- 百分比来自 Codex 已写入本地日志的服务器额度快照，不进行 Token 估算。
- 没有新的 Codex 响应时，应用只更新重置倒计时。

关闭主窗口会隐藏到系统托盘；从托盘菜单选择“退出”才会结束程序。

> Codex Orbit 是非官方个人工具，与 OpenAI 无隶属或背书关系。
