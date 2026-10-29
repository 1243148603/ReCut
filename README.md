# CutMap

> 将 Copernicus GLO-30 DEM 处理为可直接部署的 Cesium 地形瓦片，并同时导出供算法使用的 GeoTIFF 与元数据。

CutMap 是一个面向 Windows 的桌面工具和命令行工具。它把“下载 DEM → 拼接/裁剪 →
切片 → 打包”收敛为可复用的工作流，交付给前端和算法侧的产物均可独立使用。

**当前仓库版本：0.1.3** · **运行平台：Windows 10/11 x64** · **语言与框架：C# / .NET 8 / WinForms**

## 功能概览

- 下载并校验 Copernicus GLO-30 DEM 分幅，支持断点续传。
- 使用 GDAL 拼接和裁剪栅格；支持矩形范围与行政区划边界。
- 调用自包含的 `cesium-terrain-builder` 生成 Cesium `heightmap-1.0` 地形瓦片。
- 打包两类产物：前端的 `layer.json` + 瓦片目录，以及算法侧的 GeoTIFF + 元数据。
- 提供无界面 CLI 回归闸门，覆盖网格计算、坐标处理、边界持久化和产物校验。

## 快速开始

### 使用桌面程序

安装包和绿色版 ZIP 是可再生成的发布物，未提交到仓库。可通过下列命令构建：

```powershell
# 绿色版 ZIP
powershell -ExecutionPolicy Bypass -File cutmap\pack-release.ps1

# 当前用户范围的安装包（不需要管理员权限）
powershell -ExecutionPolicy Bypass -File cutmap\pack-installer.ps1
```

生成的文件位于 `dist/`。安装后按以下顺序操作：

1. **下载 DEM 数据**：选择数据源、填写范围并下载分幅；
2. **拼接裁剪**：按矩形或行政区划边界生成目标 GeoTIFF；
3. **切片**：将 GeoTIFF 转为 Cesium 地形瓦片；
4. **打包**：选择需要交付的前端和/或算法产物。

每一步也可以单独执行。例如，已有瓦片目录时只需运行“打包”。完整界面说明见
[cutmap/README.md](cutmap/README.md)。

### 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。CTB、GDAL 与
PROJ 运行时已包含在仓库的 `ctb/` 中。

```powershell
dotnet build cutmap\CutMap.sln -c Release
```

桌面程序的发布输出位于
`cutmap/src/CutMap.App/bin/Release/net8.0-windows/win-x64/`。

## 交付产物与部署

| 使用方 | 产物 | 用途 |
|---|---|---|
| 前端 | Cesium 地形瓦片目录、`layer.json`、部署说明 | 由 `CesiumTerrainProvider.fromUrl()` 直接加载。 |
| 算法侧 | 裁剪后的 GeoTIFF、同名元数据 JSON | 作为下游分析输入，无需再次执行 `gdalwarp`。 |

> [!IMPORTANT]
> CTB 输出的 `.terrain` 文件是 gzip 流。部署服务器必须返回
> `Content-Encoding: gzip`；否则请求虽可能返回 200，但 Cesium 不会正确加载地形。
> 不要使用 `python -m http.server` 预览瓦片。

本地预览请使用项目提供的服务器：

```powershell
python serve.py --port 8080
# 浏览器打开 http://127.0.0.1:8080/terrain_viewer.html
```

## 项目结构

| 位置 | 内容 |
|---|---|
| `cutmap/src/CutMap.Core/` | 与界面无关的核心：GDAL、网格、切片、打包、存储和边界处理。 |
| `cutmap/src/CutMap.App/` | WinForms 桌面程序。 |
| `cutmap/src/CutMap.Cli/` | 自动化、回归与排障入口。 |
| `cutmap/src/CutMap.Setup/` | 当前用户范围安装器。 |
| `ctb/` | 自包含的 cesium-terrain-builder、GDAL/PROJ 运行时。 |
| `resources/` | 行政区划边界、数据抓取脚本及来源说明。 |
| `baseline/` | 回归闸门使用的瓦片计数与层级标定基线。 |
| `frontend/` | 可独立构建的 Vite + Cesium 前端示例。 |
| `viewer/`、`terrain_viewer.html` | 离线可用的三维预览页及其依赖。 |
| `docs/`、`outputs/` | 正式技术文档、验收说明和可追溯证据。 |

## 验证与 CLI

`CutMap.Cli.exe` 可用于无需启动 GUI 的验证与排障：

```text
CutMap.Cli version
CutMap.Cli grid-gate
CutMap.Cli coordinate-gate
CutMap.Cli boundary-persistence-gate
CutMap.Cli verify <瓦片目录> [源 DEM]
```

当前基线的关键结果为：重庆范围 15 层合计 **223,495** 个瓦片；参考层级标定
`0.5/1/2/3` 角秒分别对应 `15/14/13/12`。更完整的命令列表、输入要求及验收边界见
[cutmap/README.md](cutmap/README.md) 和 [docs/技术架构.md](docs/技术架构.md)。

## 文档导航

| 文档 | 适用场景 |
|---|---|
| [cutmap/README.md](cutmap/README.md) | 软件使用、部署、构建、发布与 CLI 命令速查。 |
| [docs/使用说明.md](docs/使用说明.md) | 面向使用者的完整操作步骤与常见问题。 |
| [docs/技术架构.md](docs/技术架构.md) | 模块分层、调用链、数据流与外部依赖。 |
| [docs/开发与交接.md](docs/开发与交接.md) | 维护决策、已知陷阱和扩展说明。 |
| [docs/基线运行说明.md](docs/基线运行说明.md) | 基线口径、命令行差异与验收依据。 |
| [docs/通用性验收.md](docs/通用性验收.md) | 更换区域或数据源后的实测边界。 |
| [CHANGELOG.md](CHANGELOG.md) | 版本变更、验收结论与已知问题。 |

## 文件管理

以“能否可靠重建、是否仍是唯一证据”为清理标准，而不是文件大小或 Git 忽略状态。

- **必须保留**：源码、`ctb/` 运行依赖、`resources/`、`baseline/`、正式文档和
  `outputs/` 中的验收证据。
- **可再生但先核实**：`bin/`、`obj/`、`data/`、`dist/`、`cutmap/_gate/`、`work/`
  与日志目录；它们可能包含唯一的发布物或问题复现材料。
- **待人工确认**：根目录 `_memtest*`、`_memcap*`、`_ovr_*`、`_split_test*` 和
  来源不明的大型文件，均不得自动删除。

整理前先运行 `git status --short`，再使用 `git check-ignore -v <路径>` 确认规则。
不要使用 `git clean -fdx`。

## 已知限制

- 当前正式运行平台为 Windows x64。
- 全量切片耗时受硬件与文件系统环境影响；基准用例约有 22 万个输出文件。
- 行政区划数据目前使用 DataV.GeoAtlas 的简化边界；需要更高精度时应更换数据源并
  重新验证裁剪结果。
- 安装包未签名，首次运行可能出现 Windows SmartScreen 提示。

## 贡献

欢迎提交问题与改进。修改网格算法、GDAL 版本、切片逻辑或边界数据处理前，请先阅读
[docs/开发与交接.md](docs/开发与交接.md)，并运行与改动对应的 CLI 闸门。请勿提交
`data/` 下的大型 DEM、生成瓦片、构建目录或本机凭据。

## 许可证与数据署名

项目采用 [Apache License 2.0](LICENSE)。第三方组件与数据的声明见
[cutmap/THIRD-PARTY-NOTICES.txt](cutmap/THIRD-PARTY-NOTICES.txt)。分发含 DEM 或
行政区划数据的产物时，请遵守其中的 Copernicus DEM 与 DataV.GeoAtlas 署名要求。
