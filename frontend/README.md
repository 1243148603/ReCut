# CutMap 前端（Vite + CesiumJS 官方包）

把 CutMap 产出的 Cesium 地形瓦片（+ 随产物分发的边界矢量）展示出来。
用**官方 `cesium` npm 包**，可以独立部署，也可以整段嵌进已有的前端工程。

## 三步跑起来

```bash
cd frontend
npm install                 # 走不动就加 --registry=https://registry.npmmirror.com

# 把 CutMap 的瓦片目录内容放到 public/terrain/（layer.json + 0/ 1/ … + boundary.geojson）
# 或者复制 .env.example 成 .env.local，用 VITE_TERRAIN_URL 指向任意地址

npm run dev                 # http://localhost:5173
```

`.env.local` 里还能放两样（都不进 git）：

| 变量 | 作用 |
|---|---|
| `VITE_CESIUM_ION_TOKEN` | 有令牌底图默认用 **Bing 卫星影像**；没有则用本地离线影像（Natural Earth II） |
| `VITE_BOUNDARY_URL` | 边界矢量地址；默认 `<瓦片集>/boundary.geojson` |

URL 参数（调试用）：`?tiles=`、`?boundary=`、`?debug=1`（诊断面板）、`?pick=center`（画面中心自动取一次读数）。

## ⚠ 部署时最容易踩的坑：`.terrain` 必须带 `Content-Encoding: gzip`

CTB 产出的 `.terrain` 本身就是 **gzip 流**；Cesium **不在 JS 里解压**，它依赖浏览器透明解压——
而这只有当响应带 `Content-Encoding: gzip` 时才会发生。缺这个头，浏览器把压缩字节原样交给
Cesium，`HeightmapTerrainData` 构造时抛 `RangeError`，症状是**瓦片全是 200、地形却永远停在 0 级**。

- **开发**：本工程的 Vite 插件已经补上这个头（嗅探 gzip magic 再决定，未压缩的瓦片绝不误标）。
- **生产（nginx）**：
  ```nginx
  location ~ \.terrain$ {
    add_header Content-Encoding gzip;
    add_header Cache-Control no-store;
  }
  ```
  Apache：`Header set Content-Encoding gzip`（限 `.terrain`）。Vite/Apache 静态托管默认都不发这个头。
- 验证：`curl --compressed -o t.bin <url>/0/1/0.terrain` 应得 8452 字节；`curl -o t.bin` 得 86 字节的 gzip 流说明服务器没声明。

## 边界矢量（矢量裁剪产物才有）

打包时 CutMap 会把边界拷成 `<瓦片目录>/boundary.geojson`，按 `properties.kind` 分三类渲染：
`outline` 外轮廓（粗线）、`sublimit` 下级界线（细线）、`label` 行政中心标注（带 name/gb），
`source` 只做署名。界面开关：**边界**（显示/隐藏）、**边界外裁掉**（用 `ClippingPolygon`
按 outline 外环裁掉边界外——对应"裙边填充"之外的另一条展示路径，需要 WebGL2；
**注意 ClippingPolygon 只对凸多边形保证正确**，行政区划边界几乎都是凹的，
凹处会多裁/少裁，正式展示建议直接用裙边路径）。

## 目录

```
index.html          页面骨架（HUD / 控制条 / 读数 / 诊断面板）
src/config.js       URL 参数 + .env.local 配置
src/terrain.js      layer.json、CesiumTerrainProvider、瓦片请求记录
src/imagery.js      底图切换（Ion Bing / 本地离线 / OSM / 关闭）
src/boundary.js     边界矢量加载与样式、ClippingPolygon
src/hud.js          信息卡/状态/点击拾取读数
src/main.js         装配
vite.config.js      Cesium 资源拷贝、CESIUM_BASE_URL、dev 的 gzip 中间件
public/terrain/     放瓦片（不进 git）
```

## 与 CutMap 主仓库的关系

- 前端**不随产物 zip 分发**：交付包只有 瓦片 + `layer.json` + `README.txt` + `boundary.geojson`。
- 零依赖的排查入口仍在仓库根的 `terrain_viewer.html` + `serve.py`（同样的 gzip 语义、同样的
  layer.json 契约），本工程是它的"正式版"。
