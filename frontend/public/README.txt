把 CutMap 产出的瓦片目录内容放在这里（terrain/ 子目录），即：
  public/terrain/layer.json
  public/terrain/0/ 1/ … /x/y.terrain
  public/terrain/boundary.geojson   （矢量裁剪产物才有）
或者改用 .env.local 的 VITE_TERRAIN_URL 指向别处。
本目录下的 terrain/ 与 cesium/ 都在 .gitignore 里，不会入库。
