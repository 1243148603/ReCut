import * as Cesium from 'cesium';
import { BOUNDARY_URL } from './config.js';

/**
 * 加载随产物分发的边界矢量（FR-15）。文件里 features[].properties.kind 区分：
 *   outline   外轮廓（粗线）
 *   sublimit  内部下级界线（细线）
 *   label     行政中心标注点（name / gb）
 *   source    数据来源署名（geometry 为 null —— 不要对它取几何）
 *
 * 返回 dataSource；没有 boundary.geojson（矩形裁剪产物）时返回 null，不算错误。
 */
export async function loadBoundary(viewer, url = BOUNDARY_URL) {
  let ds;
  try {
    ds = await Cesium.GeoJsonDataSource.load(url, { clampToGround: true });
  } catch (e) {
    console.log('[boundary] 没有可用的边界矢量：', e.message || e);
    return null;
  }

  for (const entity of ds.entities) {
    const kind = entity.properties?.kind?.getValue?.() ?? entity.properties?.kind;
    switch (kind) {
      case 'outline':
        entity.polyline = {
          width: 2, material: Cesium.Color.WHITE, clampToGround: true,
        };
        break;
      case 'sublimit':
        entity.polyline = {
          width: 1, material: Cesium.Color.WHITE.withAlpha(0.45), clampToGround: true,
        };
        break;
      case 'label':
        entity.point = {
          pixelSize: 6, color: Cesium.Color.WHITE,
          outlineColor: Cesium.Color.BLACK, outlineWidth: 2,
          heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
        };
        entity.label = {
          text: entity.properties?.name?.getValue?.() ?? '',
          font: '13px "Microsoft YaHei UI", sans-serif',
          fillColor: Cesium.Color.WHITE,
          outlineColor: Cesium.Color.BLACK,
          outlineWidth: 3,
          style: Cesium.LabelStyle.FILL_AND_OUTLINE,
          pixelOffset: new Cesium.Cartesian2(0, -18),
          heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
          disableDepthTestDistance: Number.POSITIVE_INFINITY,
        };
        break;
      case 'source':
        entity.show = false;   // 只做署名，没有几何
        break;
      default:
        if (!kind) entity.show = false;   // 未知要素不渲染，别猜
    }
  }

  await viewer.dataSources.add(ds);
  return ds;
}

/**
 * 边界外裁成空洞（PRD FR-07 第二条路径，与"裙边填充"并列）。
 *
 * 直接把瓦片换成"边界外不显示"：用 outline 的外环构造 ClippingPolygon。
 * 需要 WebGL2；关闭时把 clippingPolygons 置回 undefined 恢复。
 *
 * **限制（务必知道）**：ClippingPolygon 只对**凸多边形**保证正确，而行政区划边界
 * 几乎都是凹的——凹处会被多裁/少裁。做演示够用；正式展示建议直接用「裙边填充」
 * 那条路径（矢量裁剪的默认产物）。
 */
export function setClipping(viewer, dataSource, enabled) {
  const globe = viewer.scene.globe;
  if (!enabled || !dataSource) {
    globe.clippingPolygons = undefined;
    return 0;
  }

  const polygons = [];
  for (const entity of dataSource.entities.values) {
    const kind = entity.properties?.kind?.getValue?.() ?? entity.properties?.kind;
    if (kind !== 'outline' || !entity.polygon) continue;
    const hierarchy = entity.polygon.hierarchy?.getValue?.(Cesium.JulianDate.now());
    const rings = hierarchy?.positions ? [hierarchy.positions] : [];
    for (const ring of rings) {
      polygons.push(new Cesium.ClippingPolygon({ positions: ring }));
    }
  }
  if (polygons.length === 0) return 0;

  globe.clippingPolygons = new Cesium.ClippingPolygonCollection({ polygons });
  console.warn('[boundary] ClippingPolygon 只保证凸多边形正确；凹的行政区划边界可能多裁/少裁');
  return polygons.length;
}
