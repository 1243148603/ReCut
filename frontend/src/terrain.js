import * as Cesium from 'cesium';
import { TERRAIN_URL } from './config.js';

/** 图层元信息（layer.json）与本次会话的瓦片请求记录。 */
export const layer = { name: '地形瓦片', description: '', version: '', bounds: null, maxzoom: null };
export const tileLog = [];   // {url, status}

const base = new URL(TERRAIN_URL, location.href);

/** 读 layer.json：镜头、层级、信息卡都从它来（写死的话换个瓦片集就全错）。 */
export async function loadLayerConfig() {
  const r = await fetch(new URL('layer.json', base), { cache: 'no-store' });
  if (!r.ok) throw new Error(`layer.json HTTP ${r.status}`);
  const j = await r.json();
  layer.name = j.name || layer.name;
  layer.description = j.description || '';
  layer.version = j.version || '';
  if (Array.isArray(j.bounds) && j.bounds.length === 4) layer.bounds = j.bounds;
  if (Number.isFinite(j.maxzoom)) layer.maxzoom = j.maxzoom;
  return layer;
}

/** 记录每个 .terrain 请求的真实状态码（Cesium 的 errorEvent 不带 URL）。 */
export function installTileLogger(shorten) {
  const record = (url, status) => {
    if (/\.terrain(\?|$)/.test(url)) tileLog.push({ url: shorten(url), status });
  };
  const origFetch = window.fetch;
  window.fetch = function (input, init) {
    const url = typeof input === 'string' ? input : (input && input.url) || String(input);
    return origFetch.apply(this, arguments).then(
      (resp) => { record(url, resp.status); return resp; },
      (err) => { record(url, 'NETWORK_FAIL'); throw err; });
  };
  // Cesium 取 arraybuffer（地形瓦片）走的是 XHR，不拦它就会"0 个请求"。
  const xhrOpen = XMLHttpRequest.prototype.open;
  const xhrSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url) {
    this.__url = url;
    return xhrOpen.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function () {
    if (/\.terrain(\?|$)/.test(this.__url || '')) {
      this.addEventListener('loadend', () => record(this.__url, this.status || 'NETWORK_FAIL'));
    }
    return xhrSend.apply(this, arguments);
  };
}

/** 加载地形 provider。heightmap-1.0 下 requestWaterMask/Normals 是空转，保留以对齐文档。 */
export async function loadTerrain(viewer, onError) {
  const provider = await Cesium.CesiumTerrainProvider.fromUrl(TERRAIN_URL, {
    requestWaterMask: true,
    requestVertexNormals: true,
  });
  viewer.scene.terrainProvider = provider;
  provider.errorEvent.addEventListener((err) => {
    const msg = (err && (err.message || (err.error && err.error.message))) || String(err);
    const where = err && err.level !== undefined ? ` @ ${err.level}/${err.x}/${err.y}` : '';
    const benign = /404|not found|does not exist/i.test(msg);
    onError?.(`terrain error${where}: ${msg}` + (benign ? '（数据范围外，正常）' : ''), benign);
  });
  return provider;
}

/** 该点所在的最深层瓦片号（CTB geodetic：z 层 2^(z+1) 列、2^z 行）。 */
export function tileOf(lon, lat, zoom) {
  const nx = 2 ** (zoom + 1), ny = 2 ** zoom;
  return [Math.floor((lon + 180) / 360 * nx), Math.floor((lat + 90) / 180 * ny)];
}
