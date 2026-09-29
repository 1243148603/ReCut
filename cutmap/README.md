# CutMap

把 Copernicus GLO-30 DEM 变成两套产物的 Windows 桌面工具：

- **前端产物** — Cesium 地形瓦片（`heightmap-1.0`）+ `layer.json`，可直接被
  `CesiumTerrainProvider` 加载
- **算法产物** — 裁剪后的 GeoTIFF + 同名元数据 json，喂给算法侧不必再手工
  `gdalwarp` 一遍

对齐 204 教程的完整链路：下载分幅 → 拼接裁剪 → 切片 → 打包。

---

## 1. 快速开始

```
CutMap.App.exe
```

首次使用建议按这个顺序：

1. 工具条 **「下载 DEM 数据…」** → 选源 → 填区域 → 看分幅数与总量 → 下载
2. 下载完会问「要现在接着做拼接裁剪吗」→ 是 → 选范围（内置重庆全域预设）→ 开始
3. 任务完成后，从 **「新建任务…」** 走「切片」→ 选裁剪后的 GeoTIFF → 看参考层级与
   预估瓦片数 → 开始
4. 再走「打包」→ 勾要哪几套产物、要不要 zip → 拿产物

三步可以各自独立跑：已经有瓦片目录的人可以直接跑「打包」写 `layer.json`，
不必重跑切片。

---

## 2. 部署产物（给前端）

产物目录长这样：

```
terrain/
├── layer.json          CesiumTerrainProvider 的入口
├── README.txt          同目录下的部署说明（打包时自动写入）
├── 0/ 1/ … 14/         {z}/{x}/{y}.terrain
```

### ⚠ 服务器必须声明 `Content-Encoding: gzip`

**这是「瓦片都生成了，前端却一片空白」的根因。** CTB 输出的 `.terrain` 是
gzip 压缩的：磁盘上 `0/1/0.terrain` 只有 86 字节，解压后才是完整的 8452 字节。
Cesium **不在 JS 里解压**，它依赖浏览器透明解压——而这只有当响应带
`Content-Encoding: gzip` 时才会发生。

缺这个头的症状极具误导性：瓦片请求全是 **200**、`layer.json` 也能正常解析，
但 globe 永远停在 0 级，屏幕上没有地形。

```nginx
location ~ \.terrain$ { gzip_static off; add_header Content-Encoding gzip; }
```

Apache：`Header set Content-Encoding gzip`。
**不要用 `python -m http.server`**——它不加这个头。

验证：`curl --compressed -o t.bin <url>` 应得到 8452 字节。

### 前端代码

```javascript
terrain: await Cesium.CesiumTerrainProvider.fromUrl('/terrain', {
  requestWaterMask: true,
  requestVertexNormals: true,
}),
```

CesiumJS 1.136 起旧的构造器与 `readyPromise` 写法已移除，只能用 `fromUrl`。

### 别把瓦片提交进 git

22 万个文件、约 1.3 GB。在前端项目的 `.gitignore` 里排除 `public/terrain/`。

---

## 3. 目录与配置

| 位置 | 内容 |
|---|---|
| `%APPDATA%\CutMap\sources.json` | 下载源（自定义的；内置源在代码里） |
| `%APPDATA%\CutMap\history.json` | 任务历史（上限 500 条） |
| `%APPDATA%\CutMap\presets.json` | 自定义预设 |
| `%APPDATA%\CutMap\logs\` | 运行日志，每次启动一个文件 |
| `resources\中国_省/市/县.geojson` | 行政区划边界，**需自备** |

配置放 `%APPDATA%` 而不是程序目录，是为了让开发态（exe 在 `bin\Debug\` 下，
重编译会清目录）与打包态行为一致。

---

## 4. 行政区划裁剪

边界数据随仓库提供（`resources\` 下三个 geojson：省 34 / 市 363 / 县 2840）。
需要更新或换源时跑：

```bash
python resources/fetch_boundaries.py
```

数据源是 **DataV.GeoAtlas**（阿里云 DataV 的公开行政区划服务，免密）。
脚本头部与 `THIRD-PARTY-NOTICES.txt` 都写明了这一点——**PRD §6 记的是天地图，
两者署名要求不同，分发产物时请按实际用的 DataV 署名**。

字段是 `name`（名称）+ `gb`（含 156 前缀的行政区划代码）。
**没有 `gb` 的要素会被自动滤掉**——那是"境界线"（如九段线），
不是行政区，选中它去裁剪得不到可解释的结果。

父子关系不是靠编码算术硬推的：直辖市（北京/天津/上海/重庆）在数据里
**没有"市辖区"那一层**，区县直接挂在市下面。程序按"候选编码往上找第一个
真实存在的节点"来解析，所以重庆的 38 个区县正常挂在重庆市下。

裁剪时边界外会填成平坦的"裙边"（默认取输入最低高程）。这不是装饰：
CTB 不处理 nodata，直接掩膜会让边界外被当成真实高程编进瓦片，
前端看到的是一片平齐的"海平面"台地而不是空洞。

---

## 5. 从源码构建

```bash
# 需要 .NET 8 SDK
dotnet build cutmap/CutMap.sln -c Release
```

产物在 `src/CutMap.App/bin/Release/net8.0-windows/win-x64/`（自包含，
用户机器不需要装 .NET 运行时）。

发布绿色版：

```bash
bash cutmap/pack-release.sh
```

会产出 `dist/CutMap-<版本>-win-x64.zip`，内含程序本体与 `ctb\`。

---

## 6. 无界面入口

`CutMap.Cli.exe` 用于回归与排障，不依赖窗口：

```bash
CutMap.Cli version      # GDAL 版本与 PROJ 数据目录
CutMap.Cli warp-gate    # 30 幅拼接，对基线校验和（期望 58775）
CutMap.Cli ctb-gate     # ctb-tile 子进程、实时进度、退出码陷阱
CutMap.Cli grid-gate    # 瓦片网格算术对基线（15 层 223,495）
CutMap.Cli warp         # 拼接裁剪 + 算法元数据
CutMap.Cli slice <tif>  # 切片
CutMap.Cli package      # 打包与双产物
CutMap.Cli download     # 分幅枚举与模板展开（--network 才真下载）
CutMap.Cli verify <dir> # 校验已有瓦片集
```

---

## 7. 已知事项

- **切片耗时**：重庆全域 22 万个瓦片实测约 9 分钟（`ctb-tile` 本身 527 s）。
  教程写的 3 分钟与基线记录的 354 s 都跑不到，原因未查——但产物逐层与基线一致，
  不影响正确性。
- **`mosaic` 不支持 gdalbuildvrt 路径**：VRT 预建会让 Checksum 变成 31298
  而不是 58775，差异集中在 1°×1° 接缝上（最大 2134 m）。实现里一次性把全部
  输入交给 warp，就是这个原因，不要"优化"成先建 VRT。
- **`ctb-tile` 失败时退出码是 0**：源码里 `catch (CTBException)` 之后照样
  `return 0`。所以本软件以 **stderr** 判成败，并把实际瓦片数与预期对账。
- **暂停只有下载支持**：切片、拼接、打包没有可插等待点的循环（PRD OUT-07）。

---

## 8. 许可

见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。要点：

- GDAL / CTB / .NET：MIT
- **Copernicus DEM GLO-30**：分发产物时须注明出处
  （`Contains modified Copernicus DEM GLO-30 data, © ESA / European Commission`）
- **行政区划数据**：来源为天地图时，随产物分发须注明数据来源
