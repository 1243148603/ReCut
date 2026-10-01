import * as Cesium from 'cesium';

const el = (id) => document.getElementById(id);

export const elements = {
  title: el('hud-title'),
  meta: el('hud-meta'),
  status: el('hud-status'),
  statusText: el('hud-status-text'),
  readout: el('readout'),
  panel: el('panel'),
  log: el('log'),
};

export function setStatus(text, kind = '') {
  elements.statusText.textContent = text;
  elements.status.className = kind;
}

export function say(text, cls = '') {
  elements.log.innerHTML += `\n<span class="${cls}">${text}</span>`;
  console.log('[cutmap]', text);
}

export function setMeta(title, metaLines) {
  elements.title.textContent = title;
  elements.meta.textContent = metaLines.join('\n');
}

/**
 * 点击拾取：经纬度 / 地形表面高程 / 瓦片号。
 *
 * 用 getPickRay + globe.pick 取与**地形网格**的交点（不是椭球面），所以高度是
 * 地形表面高度；地形瓦片还没到时退回 pickEllipsoid——经纬度照样给，高程标未知，
 * 不拿 0 冒充。
 */
export function installPicking(viewer, zoom, onLog) {
  function pickAt(x, y) {
    const ray = viewer.camera.getPickRay(new Cesium.Cartesian2(x, y));
    let cartesian = ray && viewer.scene.globe.pick(ray, viewer.scene);
    const fromTerrain = Boolean(cartesian);
    if (!cartesian) {
      cartesian = viewer.camera.pickEllipsoid(new Cesium.Cartesian2(x, y),
                                              viewer.scene.globe.ellipsoid);
    }
    if (!cartesian) return null;

    const c = Cesium.Cartographic.fromCartesian(cartesian);
    const lon = Cesium.Math.toDegrees(c.longitude);
    const lat = Cesium.Math.toDegrees(c.latitude);
    const nx = 2 ** (zoom + 1), ny = 2 ** zoom;
    const tx = Math.floor((lon + 180) / 360 * nx);
    const ty = Math.floor((lat + 90) / 180 * ny);

    elements.readout.style.display = 'block';
    elements.readout.innerHTML =
      `<b>经度</b> ${lon.toFixed(6)}°E<br>` +
      `<b>纬度</b> ${lat.toFixed(6)}°N<br>` +
      `<b>高程</b> ${fromTerrain ? c.height.toFixed(1) + ' m（地形表面）' : '未知（地形瓦片未到）'}<br>` +
      `<b>瓦片</b> z${zoom} · ${tx}/${ty}`;
    onLog?.(`拾取 ${lon.toFixed(6)},${lat.toFixed(6)} z${zoom} ${tx}/${ty}`);
    return { lon, lat, height: fromTerrain ? c.height : null, tile: [tx, ty] };
  }

  const handler = new Cesium.ScreenSpaceEventHandler(viewer.scene.canvas);
  handler.setInputAction((click) => pickAt(click.position.x, click.position.y),
                         Cesium.ScreenSpaceEventType.LEFT_CLICK);
  // ?pick=center：画面中心自动取一次读数（与点击同一路径），供自动化验收。
  return { pickAt, pickCentre: () => pickAt(viewer.canvas.clientWidth / 2,
                                            viewer.canvas.clientHeight / 2) };
}
