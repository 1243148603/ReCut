import { defineConfig } from 'vite';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const cesiumSrc = path.join(root, 'node_modules/cesium/Build/Cesium');
const cesiumDst = path.join(root, 'public/cesium');

/**
 * 把 Cesium 的运行时资源拷到 public/cesium/。
 *
 * 为什么不用 vite-plugin-cesium 之类的插件：它做的事就是这次拷贝 + 设
 * CESIUM_BASE_URL，自己写十行更透明（插件版本跟着 Vite 走，偶发 ESM 互操作问题，
 * 自定义 base 时不好排错）。拷贝进 publicDir 的好处：dev 与 build 走同一条路，
 * 行为一致；拷过一次就跳过。
 */
function cesiumAssets() {
  return {
    name: 'cutmap-cesium-assets',
    buildStart() {
      if (!fs.existsSync(cesiumSrc)) {
        this.warn('node_modules/cesium 不存在，先 npm install（Cesium 资源不完整）');
        return;
      }
      if (fs.existsSync(path.join(cesiumDst, 'Workers'))) return;
      fs.mkdirSync(cesiumDst, { recursive: true });
      for (const dir of ['Assets', 'Workers', 'Widgets', 'ThirdParty']) {
        fs.cpSync(path.join(cesiumSrc, dir), path.join(cesiumDst, dir), { recursive: true });
      }
      this.warn('已把 Cesium 运行时资源拷到 public/cesium/');
    },
  };
}

/**
 * 开发服务器补 `Content-Encoding: gzip`（生产用 nginx，见 README）。
 *
 * `.terrain` 是 CTB 产出的 gzip 流；Cesium **不在 JS 里解压**，它依赖浏览器
 * 透明解压——而这只有当响应带 Content-Encoding: gzip 时才发生。缺这个头，
 * 浏览器把压缩字节原样交给 Cesium，构造 HeightmapTerrainData 时抛 RangeError，
 * 表现为"瓦片全是 200、地形却永远停在 0 级"。
 *
 * 与 serve.py 同一套语义：**嗅探 magic 再决定**（扩展名不代表压缩——
 * 将来若产出未压缩的瓦片集，也绝不能标成 gzip），并对 .terrain/.json 加
 * Cache-Control: no-store（否则浏览器启发式缓存会把改之前的坏响应一直用下去）。
 */
function terrainGzip() {
  return {
    name: 'cutmap-terrain-gzip',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = (req.url || '').split('?')[0];
        if (!url.endsWith('.terrain') && !url.endsWith('.json')) return next();
        res.setHeader('Cache-Control', 'no-store');
        if (!url.endsWith('.terrain')) return next();
        try {
          const file = path.join(server.config.publicDir, decodeURIComponent(url));
          const magic = Buffer.alloc(2);
          const fd = fs.openSync(file, 'r');
          try { fs.readSync(fd, magic, 0, 2, 0); } finally { fs.closeSync(fd); }
          if (magic[0] === 0x1f && magic[1] === 0x8b) {
            res.setHeader('Content-Encoding', 'gzip');
          }
        } catch { /* 文件不存在就走后面的 404 */ }
        next();
      });
    },
  };
}

export default defineConfig({
  // 相对 base：构建产物丢到任意路径（nginx 子目录、public/terrain 旁边）都能用。
  base: './',
  define: {
    // Cesium 用它拼 Assets/Workers/Widgets 的地址（见上面拷贝）。
    CESIUM_BASE_URL: JSON.stringify('/cesium/'),
  },
  plugins: [cesiumAssets(), terrainGzip()],
  server: { port: 5173 },
});
