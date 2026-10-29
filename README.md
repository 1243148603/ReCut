# CutMap — DEM 到 Cesium 地形瓦片的完整工程

把 Copernicus GLO-30 的 DEM 分幅，经「下载 → 拼接裁剪 → 切片 → 打包」，
产出两套可以直接交付的产物：

| 产物 | 内容 | 给谁 |
|---|---|---|
| **前端产物** | Cesium 地形瓦片（`heightmap-1.0`）+ `layer.json` | 前端，`CesiumTerrainProvider` 直接加载 |
| **算法产物** | 裁剪后的 GeoTIFF + 同名元数据 json | 算法侧，不必再手工 `gdalwarp` 一遍 |

软件本体是一个免安装的 Windows 桌面程序（**CutMap**），也可以只用命令行工具。

---

## 快速开始

### 装

安装包与绿色版 zip **不在仓库里**（二进制，且可由源码一条命令重建）。
自己打一份：

```powershell
powershell -ExecutionPolicy Bypass -File cutmap\pack-installer.ps1   # 安装包 exe
powershell -ExecutionPolicy Bypass -File cutmap\pack-release.ps1     # 绿色版 zip
```

产物落在 `dist\`。安装包双击即装，**不需要管理员权限**（装到
`%LOCALAPPDATA%\Programs\CutMap`），开始菜单与桌面会有快捷方式，
可以从「设置 → 应用」卸载。

> 手上已经有 `dist\CutMap-<版本>-Setup-win-x64.exe` 的话，直接双击那个。

### 用

1. 工具条 **「下载 DEM 数据…」** → 选源 → 填区域 → 看分幅数与总量 → 下载
2. 下载完会问「要现在接着做拼接裁剪吗」→ 是 → 选范围或行政区划边界 → 开始
3. **「新建任务…」** → 切片 → 选裁剪后的 GeoTIFF → 看参考层级与预估瓦片数 → 开始
4. **「新建任务…」** → 打包 → 勾要哪几套产物、要不要 zip

三步可以各自独立跑。已经有瓦片目录的人可以直接跑「打包」写 `layer.json`，
不必重跑切片。

**完整操作说明见 [cutmap/README.md](cutmap/README.md)**，其中
「部署产物」一节有一条必须照做的注意事项（服务器要发 `Content-Encoding: gzip`，
否则地形永远不显示）。

---

## 文档索引

| 文档 | 给谁 |
|---|---|
| **本文件** | 所有人——这是什么、怎么跑起来 |
| [docs/技术架构.md](docs/技术架构.md) | 要看懂结构的人——**系统全景、项目分层、运行时调用链、预览页位置**（图里是真实文件名与函数名） |
| [docs/技术路线与交付.md](docs/技术路线与交付.md) | 要评估这个项目的人——选型理由、实施路线、交付清单、验收结论 |
| [docs/开发与交接.md](docs/开发与交接.md) | 要改这个项目的人——架构、决策理由、**10 个踩过的坑**、怎么扩展 |
| [docs/基线运行说明.md](docs/基线运行说明.md) | 要对照命令行做法的人——链路完整记录、教程 6 处勘误、gzip 部署陷阱 |
| [docs/通用性验收.md](docs/通用性验收.md) | 要换区域/换数据的人——**换掉重庆之后哪些能用、哪些断掉**的实测记录 |
| [docs/使用说明.md](docs/使用说明.md) | **要用这个软件的人——详细到每一步**：装、四步流程、只跑其中一步、部署、排障 |
| [cutmap/README.md](cutmap/README.md) | 软件的构建与发布、无界面命令速查 |
| [CHANGELOG.md](CHANGELOG.md) | 要对接的人——版本历史、验收结论、已知问题 |
| [cutmap/THIRD-PARTY-NOTICES.txt](cutmap/THIRD-PARTY-NOTICES.txt) | 要合规审查的人——第三方组件与数据来源的许可 |
| [resources/天地图-Key申请教程.md](resources/天地图-Key申请教程.md) | 想换回天地图数据源的人 |

---

## 仓库里有什么

```
D:\Map\
├── cutmap\          软件本体（C# / .NET 8 / WinForms）
│   ├── src\CutMap.Core\   与界面无关的内核：GDAL 封装、网格算术、任务、存储
│   ├── src\CutMap.App\    WinForms 界面
│   ├── src\CutMap.Cli\    无界面诊断与回归入口
│   ├── src\CutMap.Setup\  安装程序
│   └── README.md          软件使用手册
│
├── ctb\             cesium-terrain-builder 0.4.1 自包含分发包（119 MB）
│                    4 个 exe + 49 个 DLL + GDAL/PROJ 支撑数据。
│                    重建需要 VS2022 + conda，所以直接入库（NFR-02 的交付物）
│
├── resources\       行政区划边界数据 + 抓取脚本 + 天地图 Key 申请教程
│                    省 34 / 市 363 / 县 2840（DataV.GeoAtlas）
│
├── baseline\        基线运行的对拍基准（**已精简**到闸门要用的三个文件：
│                    逐层瓦片数、层级标定结论、一个标定样本）
│                    完整的基线留档见 git 历史，说明见 docs\基线运行说明.md
│
├── docs\            项目文档（见上方「文档索引」）
├── outputs\         已归档的验收说明与可追溯证据（受版本控制）
├── CHANGELOG.md     版本历史、验收结论、已知问题
├── .editorconfig    代码与格式约定（换行由 .gitattributes 管，两处不重复）
│
├── data\            数据（不进仓库，可由脚本重建）
├── serve.py         本地静态服务器——**查看瓦片必须用它**，见下
└── terrain_viewer.html   三维预览页（含自检面板）
```

---

## 文件管理与安全清理

仓库以“是否能可靠重建、是否仍是唯一证据”为准，而不是以文件大小或是否被 Git
忽略为准。以下边界与 `.gitignore` 保持一致：

| 类别 | 位置 | 处理方式 |
|---|---|---|
| 必须保留 | `cutmap/`、`ctb/`、`resources/`、`baseline/`、`docs/`、`outputs/`、`viewer/`、前端源码和根目录说明 | 源码、运行依赖、基线、正式文档或有效验收证据；不得按名称或扩展名批量清理。 |
| 可重建产物 | `cutmap/**/bin/`、`cutmap/**/obj/`、`data/`、`dist/`、`cutmap/_gate/`、`cutmap/_gen/`、`logs/`、`output/`、`work/` | 默认不提交；清理前仍须确认没有唯一的发布包、问题复现材料或验收证据。构建目录可由 `dotnet build cutmap/CutMap.sln -c Release` 重建，发布物可由 `pack-*.ps1` 重建。 |
| 本机缓存 | `__pycache__/`、`.playwright-cli/`、编辑器目录 | 可再生成；只删除未被验收文档或排障记录引用的条目。 |
| 待人工确认 | 根目录的 `_memtest*`、`_memcap*`、`_ovr_*`、`_split_test*`，以及来源不明的大型数据 | 这些可能是性能、覆盖或分片复现材料；即使未跟踪也不得自动删除。 |

日常整理先运行 `git status --short` 确认用户修改，再用
`git check-ignore -v <路径>` 确认生成规则。不要使用 `git clean -fdx`，也不要把
“已忽略”等同于“可以立即删除”。临时验收结果应先提炼必要结论到现有正式文档或
`outputs/`，再决定是否移除原始中间文件。

---

## 看运行结果

```bash
cd D:/Map && python serve.py --port 8080
# 浏览器打开 http://127.0.0.1:8080/terrain_viewer.html
```

> ⚠ **不要用 `python -m http.server`，也不要用编辑器的文件预览。**
> CTB 输出的 `.terrain` 是 gzip 流，Cesium 依赖浏览器透明解压，
> 而这只有当服务器发 `Content-Encoding: gzip` 时才会发生。
> 缺这个头，瓦片请求全是 200、`layer.json` 也解析正常，**但屏幕上一片空白**。
> 页面自带自检面板会直接点出这类故障。

---

## 验收结论

软件的所有关键行为都对着基线验过，不是"看起来能用"：

| 项 | 判据 | 结果 |
|---|---|---|
| 拼接裁剪与命令行一致 | Checksum **58775**、尺寸 17640×14760 | ✅ |
| 切片与命令行一致 | **223,495** 个瓦片，15 层逐层相等 | ✅ |
| 参考层级公式 | 4 组标定 15/14/13/12 全中 | ✅ |
| `ctb-tile` 失败可检出 | 实测复现「抛异常却返回 0」 | ✅ |
| 下载断点续传 | 截断到 1/3 后只取剩余 29.5 MB（整幅 44.2） | ✅ |
| 双产物 | 只前端/只算法/两套三种组合都对 | ✅ |
| 矢量裁剪 | 真实重庆市边界，裙边四角同值 51.0 m | ✅ |
| 前端真能加载 | Cesium 逐级细分到 z14，瓦片解压 8452 字节 | ✅ |

复跑这些：见 [cutmap/README.md](cutmap/README.md) 的「无界面入口」一节。

---

## 两份上游文档

本工程的来源是两份文档，它们都在 `C:\Users\hwj\Desktop\`：

| 文档 | 定义什么 |
|---|---|
| `204DEM切片与地形瓦片教程.md` | **怎么做**——命令行四步流程 |
| `PRD.md`（"Recut" V1.5） | **做成什么样**——17 个 P0 功能需求 |

**本仓库是这两份文档的实现与验证**，但有三处刻意的偏离，都在文档里写明了：

1. **教程有 6 处错误**，基线逐条证伪（`-m` 不是层级、瓦片数是 223,495 不是 223,496、
   复制目录即可部署是错的……）。软件照基线，不照教程原文。
   见 [docs/基线运行说明.md](docs/基线运行说明.md) 的「已知问题与注意事项」。
2. **数据源从天地图改为 DataV.GeoAtlas**。天地图的 Key 已实际申请并实测，
   卡在它自己的权限类型判定上（控制台显示服务器端，网关返回 301013 说浏览器端）。
   见 [resources/天地图-Key申请教程.md](resources/天地图-Key申请教程.md)。
3. **行政区划精度是简化版**。前端界线若用同一份数据绘制则完全吻合；
   要更高精度需换数据源（`resources/fetch_boundaries.py` 换源即可，不改代码）。

---

## 环境要求

| | |
|---|---|
| 运行 | Windows 10/11 x64。**不需要**装 .NET、Anaconda、GDAL（自包含） |
| 从源码构建 | .NET 8 SDK；跑 `baseline/` 的脚本另需 conda 环境 `geo` |
| 重建 ctb\ | VS2022 + CMake + conda GDAL 3.13.3，见 `docs/基线运行说明.md` §2 |

---

## 许可

本仓库包含 [Apache License 2.0](LICENSE)。第三方组件与数据的单独声明见
[cutmap/THIRD-PARTY-NOTICES.txt](cutmap/THIRD-PARTY-NOTICES.txt)；分发时仍须遵守
其中对 DEM 与行政区划数据的署名要求。

要点：

- GDAL / .NET：MIT；CTB：Apache License 2.0（见随包 CTB-LICENSE.txt）
- **Copernicus DEM GLO-30**：分发产物时须注明出处
  （`Contains modified Copernicus DEM GLO-30 data, © ESA / European Commission`）
- **行政区划数据**：来源为 **DataV.GeoAtlas**（阿里云 DataV），
  随产物分发须注明该来源
