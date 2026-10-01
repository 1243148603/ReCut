// 运行配置：URL 参数优先，其次 .env.local（不入库），最后内置默认。
const env = import.meta.env;
const params = new URLSearchParams(location.search);

/** 瓦片集地址。默认 public/terrain/（把 CutMap 的瓦片目录内容放进去即可）。 */
export const TERRAIN_URL = params.get('tiles') ?? env.VITE_TERRAIN_URL ?? '/terrain/';

/** Cesium Ion 令牌。只从 .env.local 读——**不要写进源码/提交进仓库**。 */
export const ION_TOKEN = env.VITE_CESIUM_ION_TOKEN ?? '';

/**
 * 边界矢量。打包时 CutMap 会把矢量裁剪的边界统一拷成
 * `<瓦片目录>/boundary.geojson`，所以默认就按这个名字找；没有的话
 * （矩形裁剪产物）加载会失败并被安静跳过。
 */
export const BOUNDARY_URL = params.get('boundary') ?? env.VITE_BOUNDARY_URL ??
  new URL('boundary.geojson', new URL(TERRAIN_URL, location.href)).href;

export const DEBUG = params.has('debug');
export const AUTO_PICK = params.has('pick');
