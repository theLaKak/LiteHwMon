# 第三方组件与许可

本仓库包含或依赖以下第三方组件。分发本程序（含二进制发布包）时请一并保留本文件。

## 随仓库分发的第三方文件

| 文件 | 来源 | 许可证 | 说明 |
| --- | --- | --- | --- |
| `src/LiteHwMon/Resources/WinRing0x64.sys` | 提取自 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 的内嵌资源 | **MPL-2.0** | 未做任何修改，仅用于 RAPL MSR 只读访问（CPU 功耗回退）。该文件按 MPL-2.0 分发，其对应源代码可从上游项目获取 |

## 运行时依赖（通过 NuGet 引用，不随仓库分发源码）

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | 0.9.6 | **MPL-2.0** | 传感器枚举与硬件访问（通过 PawnIO 驱动） |
| [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) | 2.0.1 | MIT | 系统托盘图标与菜单 |
| [System.Management](https://dot.net/) | 10.0.2 | MIT | WMI 查询（SMBIOS / 磁盘容量） |
| .NET 8 Desktop Runtime | 8.0 | MIT | 运行时 |

## 外部程序（不捆绑、不分发）

| 组件 | 许可证 | 说明 |
| --- | --- | --- |
| [PawnIO](https://pawnio.eu/) | 见上游项目 | 由用户自行安装的内核驱动，是 LibreHardwareMonitor 0.9.5+ 的硬件访问后端。本程序仅在运行时检测它，并可由用户触发从官方地址下载安装；本仓库**不包含**其任何文件 |

## 发布本程序时的注意事项

1. 保留本文件，以及 `src/LiteHwMon/Resources/WinRing0x64.sys` 的原始形式（MPL-2.0 要求对 MPL 覆盖文件的修改需公开源码；本程序未修改该文件）。
2. 本程序自身代码的许可证由仓库作者指定，见 `LICENSE`；该许可证不影响上述第三方组件各自的许可条款。
3. 若把 PawnIO 安装包一并打包进发布产物，需另行遵守其许可条款；默认发布流程不包含该文件。
