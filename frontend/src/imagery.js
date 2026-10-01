import * as Cesium from 'cesium';
import { ION_TOKEN } from './config.js';

let currentLayer = null;

/** 有没有可用的 Ion 令牌（决定默认底图与下拉里那一项是否可用）。 */
export function ionReady() {
  return Boolean(ION_TOKEN);
}

export function setIonToken() {
  if (ION_TOKEN) Cesium.Ion.defaultAccessToken = ION_TOKEN;
}

/**
 * 切换底图。
 *   ion   Cesium Ion 影像（Bing 卫星图，在线；官方资产 asset 2）
 *   local Cesium 自带的 Natural Earth II（0–2 级，随包分发、离线可用）
 *   osm   在线 OSM（国内网络常连不上）
 *   none  关闭
 */
export async function setBasemap(viewer, kind, alpha = 1) {
  viewer.imageryLayers.removeAll();
  currentLayer = null;
  if (kind === 'none') return null;

  try {
    if (kind === 'ion') {
      if (!ionReady()) throw new Error('没有 Cesium Ion 令牌（.env.local 的 VITE_CESIUM_ION_TOKEN）');
      const provider = await Cesium.IonImageryProvider.fromAssetId(2);
      currentLayer = viewer.imageryLayers.addImageryProvider(provider);
    } else if (kind === 'osm') {
      currentLayer = viewer.imageryLayers.addImageryProvider(
        new Cesium.OpenStreetMapImageryProvider({ url: 'https://tile.openstreetmap.org/' }));
    } else {
      const provider = await Cesium.TileMapServiceImageryProvider.fromUrl(
        Cesium.buildModuleUrl('Assets/Textures/NaturalEarthII'));
      currentLayer = viewer.imageryLayers.addImageryProvider(provider);
    }
    currentLayer.alpha = alpha;
    return currentLayer;
  } catch (e) {
    console.warn('[imagery] 底图加载失败：', e.message || e);
    return null;
  }
}

export function setOpacity(value) {
  if (currentLayer) currentLayer.alpha = value;
}

export function hasImagery() {
  return Boolean(currentLayer);
}
