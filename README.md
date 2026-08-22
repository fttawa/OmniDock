# OmniDock

一个 Windows 桌面悬浮窗，实时显示 Claude 额度用量：占比、已用 / 限额、以及距离重置的倒计时。

- **毛玻璃背景**，无边框、可整体拖动、置顶
- **数字滚动**（里程表效果），只有变化的那一位会滚
- **可配置**：界面缩放、窗口宽度、玻璃厚度、刷新间隔、强调色、置顶开关
- 窗口位置和设置自动记住

## 运行环境

- Windows 10 1803 及以上（毛玻璃依赖 `SetWindowCompositionAttribute`）
- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或对应的桌面运行时

## 构建

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\OmniDock.exe
```

## 数据来源

额度数据来自本机 Claude 客户端附带的本地代理，端点是 `GET /v1/limits`。代理监听的端口每次启动都不同，程序按
「`ANTHROPIC_BASE_URL` 环境变量 → 上次成功的端点 → 反查代理进程监听的回环端口」依次尝试，
不读取也不存储任何凭据。

拿不到数据时窗口会显示提示，并按退避间隔重试，不会一直空转。

## 快捷键

| 键 | 作用 |
|---|---|
| `F2` | 打开 / 收起设置 |
| `F5` | 立即刷新 |
| `Esc` | 关闭 |

设置存放在 `%LOCALAPPDATA%\OmniDock\settings.json`。
