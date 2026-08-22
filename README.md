# OmniDock

一个 Windows 桌面悬浮窗，实时显示 Mirasim 用量：占比、已用 / 限额、以及距离重置的倒计时。

- **毛玻璃背景**，无边框、可整体拖动、置顶
- **数字滚动**（里程表效果），只有变化的那一位会滚
- **速率预测**：按最近的消耗速度推算会不会在重置前用光
- **额度告警**：水位过高或预计撞墙时脉冲提示
- **托盘图标**：把占比画成进度环，窗口收起也能扫一眼
- **可配置**：界面缩放、窗口宽度、玻璃厚度、刷新间隔、强调色、置顶、告警、开机自启、关闭到托盘
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

用量数据来自本机 Mirasim 的本地代理，端点是 `GET /v1/limits`，读的是 Mirasim 中转的额度而不是
Claude 账号自身的额度。代理监听的端口每次启动都不同，程序按
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
