import * as Cesium from 'cesium';
import 'cesium/Build/Cesium/Widgets/widgets.css';
import './style.css';

import { TERRAIN_URL, DEBUG, AUTO_PICK } from './config.js';
import {
  layer, tileLog, loadLayerConfig, installTileLogger, loadTerrain, tileOf,
} from './terrain.js';
import { loadBoundary, setClipping } from './boundary.js';
import { ionReady, setIonToken, setBasemap, setOpacity } from './imagery.js';
import { elements, setStatus, say, setMeta, installPicking } from './hud.js';

// 顶层 await 在 Vite 默认构建目标（es2020）下不可用，包一层 async IIFE。
(async function main() {
  const el = (id) => document.getElementById(id);
  const params = new URLSearchParams(location.search);
  const base = new URL(TERRAIN_URL, location.href);
  const shorten = (u) => String(u).replace(base.href, '').replace(location.origin, '');
  if (DEBUG) elements.panel.classList.remove('hidden');

  installTileLogger(shorten);

  // ── 图层元信息（先把 layer.json 读出来，镜头/信息卡/瓦片号都靠它）──────────
  try {
    await loadLayerConfig();
  } catch (e) {
    say(`layer.json 读取失败：${e.message || e}（用兜底范围）`, 'err');
    layer.bounds = layer.bounds ?? [105.3, 28.1, 110.2, 32.2];
    layer.maxzoom = layer.maxzoom ?? 14;
  }
  const [west, south, east, north] = layer.bounds;
  const centre = { lon: (west + east) / 2, lat: (south + north) / 2 };
  setMeta(layer.name, [
    layer.description ?? '',
    `范围 [${west.toFixed(3)}, ${south.toFixed(3)}, ${east.toFixed(3)}, ${north.toFixed(3)}]  zoom 0-${layer.maxzoom}` +
      (layer.version ? `  v${layer.version}` : ''),
  ].filter(Boolean));

  // ── 视图 ────────────────────────────────────────────────────────────────
  setIonToken();
  const viewer = new Cesium.Viewer('cesiumContainer', {
    baseLayer: false,
    animation: false, timeline: false, baseLayerPicker: false, geocoder: false,
    sceneModePicker: false, navigationHelpButton: false, fullscreenButton: false,
    infoBox: false, selectionIndicator: false,
  });
  viewer.scene.verticalExaggeration = 2;
  window.viewer = viewer;          // 便于控制台/自动化排查
  window.tileLog = tileLog;

  const flyHome = () => viewer.camera.flyTo({
    destination: Cesium.Cartesian3.fromDegrees(centre.lon, centre.lat, 180000),
    orientation: { heading: 0, pitch: Cesium.Math.toRadians(-55), roll: 0 },
    duration: 0,
  });

  // ── 地形 ────────────────────────────────────────────────────────────────
  setStatus('正在加载地形…');
  let provider;
  try {
    provider = await loadTerrain(viewer, (msg, benign) => {
      say(msg, benign ? 'dim' : 'err');
      if (!benign) setStatus(msg, 'err');
    });
  } catch (e) {
    say(`地形 provider 创建失败：${e.message || e}`, 'err');
    setStatus(`地形加载失败：${e.message || e}`, 'err');
    throw e;
  }

  // ── 底图（有 Ion 令牌默认 Bing；没有则本地离线影像）──────────────────────
  const basemapSelect = el('basemap');
  // ?basemap=ion|local|none|osm（与仓库根的预览页对齐；自动化验收常用 local）
  const basemapParam = params.get('basemap');
  if (basemapParam && [...basemapSelect.options].some((o) => o.value === basemapParam)) {
    basemapSelect.value = basemapParam;
  }
  const opacity = el('opacity');
  const opacityText = el('opacity-text');
  if (!ionReady()) {
    const option = basemapSelect.querySelector('option[value="ion"]');
    option.disabled = true;
    option.textContent = 'Cesium Ion 影像（无令牌）';
    basemapSelect.value = 'local';
  }
  let basemapKind = basemapSelect.value;
  await setBasemap(viewer, basemapKind, Number(opacity.value) / 100);
  say(basemapKind === 'ion'
    ? '底图：Cesium Ion 影像（Bing 卫星图，在线）'
    : '底图：本地离线影像（Natural Earth II）', 'ok');

  basemapSelect.addEventListener('change', async () => {
    basemapKind = basemapSelect.value;
    await setBasemap(viewer, basemapKind, Number(opacity.value) / 100);
    say(`底图切换：${basemapSelect.options[basemapSelect.selectedIndex].text}`, 'dim');
  });
  opacity.addEventListener('input', () => {
    setOpacity(Number(opacity.value) / 100);
    opacityText.textContent = opacity.value + '%';
  });

  // ── 边界矢量（FR-15）+ 边界外裁剪（FR-07 第二条路径）────────────────────
  const boundaryToggle = el('boundary');
  const clipToggle = el('clip');
  const boundaryData = await loadBoundary(viewer);
  if (boundaryData) {
    const kinds = {};
    for (const e of boundaryData.entities.values) {
      const k = e.properties?.kind?.getValue?.() ?? '(无 kind)';
      kinds[k] = (kinds[k] ?? 0) + 1;
    }
    say('边界矢量：' + Object.entries(kinds).map(([k, n]) => `${k}×${n}`).join('、'), 'ok');
  } else {
    boundaryToggle.disabled = true;
    clipToggle.disabled = true;
    boundaryToggle.checked = false;
    say('没有 boundary.geojson（矩形裁剪产物不带边界），边界相关开关已禁用。', 'dim');
  }
  // ?clip=1 启动即开"边界外裁掉"，?noboundary=1 启动即隐藏边界（自动化验收用）。
  if (params.has('clip')) { clipToggle.checked = true; clipToggle.dispatchEvent(new Event('change')); }
  if (params.has('noboundary')) { boundaryToggle.checked = false; boundaryToggle.dispatchEvent(new Event('change')); }

  boundaryToggle.addEventListener('change', () => {
    if (boundaryData) boundaryData.show = boundaryToggle.checked;
  });
  clipToggle.addEventListener('change', () => {
    const n = setClipping(viewer, boundaryData, clipToggle.checked);
    if (clipToggle.checked && n === 0) {
      say('没能从 outline 取出裁剪环（ClippingPolygon 未生效）', 'warn');
    } else {
      say(clipToggle.checked ? `边界外已裁掉（${n} 个环）` : '边界外恢复显示（裙边）', 'dim');
    }
  });

  el('lighting').addEventListener('change', (e) => {
    viewer.scene.globe.enableLighting = e.target.checked;
  });
  el('exaggeration').addEventListener('input', (e) => {
    viewer.scene.verticalExaggeration = Number(e.target.value);
    el('exaggeration-text').textContent = Number(e.target.value).toFixed(1) + 'x';
  });
  el('fly-home').addEventListener('click', flyHome);
  el('toggle-debug').addEventListener('click', () => elements.panel.classList.toggle('hidden'));

  flyHome();

  // ── 瓦片加载完成的汇总（等"忙过再归零"，避免相机就位前的假 0）────────────
  let settle = null, sawActivity = false, finished = false;
  function finish() {
    if (finished) return;
    finished = true;
    const ok = tileLog.filter((t) => t.status === 200).length;
    const missing = tileLog.filter((t) => t.status === 404).length;
    say(`瓦片请求 ${tileLog.length} 个（200×${ok}，404×${missing}）`, ok > 2 ? 'ok' : 'err');
    if (ok > 2) {
      setStatus(`地形加载完成 · ${ok} 块瓦片`, 'ok');
    } else {
      setStatus(`只取回 ${ok} 块瓦片：检查服务器是否发 Content-Encoding: gzip`, 'err');
    }
  }
  viewer.scene.globe.tileLoadProgressEvent.addEventListener((n) => {
    clearTimeout(settle);
    if (n > 0) { sawActivity = true; settle = null; setStatus(`正在取瓦片（队列 ${n}）…`); }
    else if (sawActivity) settle = setTimeout(finish, 2000);
  });
  setTimeout(finish, 30000);

  // ── 拾取（点击地形给读数；?pick=center 供自动化验收）─────────────────────
  const picking = installPicking(viewer, layer.maxzoom, (line) => say(line, 'dim'));
  if (AUTO_PICK) {
    let tries = 0;
    const timer = setInterval(() => {
      const r = picking.pickCentre();
      if (r || ++tries > 12) clearInterval(timer);
    }, 2000);
  }
})();
