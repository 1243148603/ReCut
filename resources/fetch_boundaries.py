#!/usr/bin/env python
# -*- coding: utf-8 -*-
r"""
fetch_boundaries.py -- 抓取全国省/市/县三级行政边界，产出 CutMap 用的三个 geojson。

数据源：DataV.GeoAtlas（阿里云 DataV 的公开行政区划服务）
        https://geo.datav.aliyun.com/areas_v3/bound/{adcode}_full.json
        免注册、免密钥。

**来源与 PRD 的出入（重要）**：PRD §6 记录的原始数据源是**天地图**
（国家地理信息公共服务平台）。天地图的行政区划服务需要申请 tk 密钥，无法在
无人值守的脚本里直接取。DataV 是免密的公开源，字段与几何类型兼容本程序，
但**数据来源的署名要求不同** —— 随产物分发时的出处声明必须写实际用的这个源，
不能照抄 PRD 里的天地图。见 THIRD-PARTY-NOTICES.txt。

产出（写到脚本同目录）：
    中国_省.geojson    省级
    中国_市.geojson    市级（直辖市不产生这一级）
    中国_县.geojson    区县级

字段映射（本程序要 name + gb）：
    name   ← DataV 的 name，原样
    gb     ← "156" + adcode，即含国家码前缀的行政区划代码。
             CutMap 的父子关系靠它推（156|5001|13 → 上级 156|5001|00），
             所以前缀不能省。
    没有数字 adcode 的要素（如九段线 100000_JD）**不写 gb** —— 它们会在
    载入时被 CutMap 当作"境界线"滤掉，与 PRD §6 描述的行为一致。

用法：
    python fetch_boundaries.py            # 全量抓取
    python fetch_boundaries.py --resume   # 断点续抓（用缓存）
"""

import argparse
import concurrent.futures
import io
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = "https://geo.datav.aliyun.com/areas_v3/bound/{}_full.json"
ROOT = "100000"          # 全国
COUNTRY_PREFIX = "156"   # 中国在国家行政区划代码里的前缀

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, ".cache")
RETRIES = 3
TIMEOUT = 30
WORKERS = 6

# 九段线这类要素的 adcode 不是纯数字（100000_JD），它们没有行政区划代码。
_NON_NUMERIC = object()


def fetch(adcode, session_headers=None):
    """取一个 adcode 的 _full.json。取不到返回 None（该级不存在是正常情况）。"""
    url = BASE.format(adcode)
    cache_path = os.path.join(CACHE, adcode + ".json")
    if os.path.exists(cache_path):
        with io.open(cache_path, encoding="utf-8") as f:
            return json.load(f)

    last = None
    for attempt in range(RETRIES):
        try:
            req = urllib.request.Request(url, headers=session_headers or {})
            with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
                raw = resp.read().decode("utf-8")
            data = json.loads(raw)
            os.makedirs(CACHE, exist_ok=True)
            with io.open(cache_path, "w", encoding="utf-8") as f:
                f.write(raw)
            return data
        except urllib.error.HTTPError as e:
            # 404 表示这一级没有下级（例如某些县级市），不是错误。
            if e.code == 404:
                return None
            last = e
        except Exception as e:  # noqa: BLE001 - 网络异常种类多，统一重试
            last = e
        time.sleep(1.5 * (attempt + 1))
    sys.stderr.write("  [warn] {} 取不到：{}\n".format(adcode, last))
    return None


def gb_of(props):
    """adcode → 含国家码前缀的 gb。没有数字 adcode 的返回 None（境界线）。"""
    adcode = props.get("adcode")
    text = str(adcode) if adcode is not None else ""
    if not text.isdigit():
        return None
    return COUNTRY_PREFIX + text


def feature_of(feature):
    """把 DataV 的要素改写成 CutMap 认的形态。"""
    props = feature.get("properties") or {}
    name = props.get("name") or ""
    gb = gb_of(props)
    out = {"type": "Feature", "properties": {"name": name}}
    if gb:
        out["properties"]["gb"] = gb
    out["geometry"] = feature.get("geometry")
    return out


def collection(features, title):
    return {
        "type": "FeatureCollection",
        "name": title,
        # CGCS2000 经纬度（EPSG:4490），与 WGS84 DEM 兼容、无 GCJ-02 偏移。
        "crs": {"type": "name", "properties": {"name": "urn:ogc:def:crs:EPSG::4490"}},
        "features": features,
    }


def write(name, features):
    path = os.path.join(HERE, name)
    with io.open(path, "w", encoding="utf-8") as f:
        json.dump(collection(features, name.replace(".geojson", "")), f,
                  ensure_ascii=False, separators=(",", ":"))
    size = os.path.getsize(path) / (1024.0 * 1024)
    print("  {} : {} 个要素，{:.1f} MB".format(name, len(features), size))
    return len(features)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--resume", action="store_true",
                        help="用 .cache 里的缓存，不重新下载已有的")
    args = parser.parse_args()
    if not args.resume and os.path.exists(CACHE):
        import shutil
        shutil.rmtree(CACHE)

    print("一、全国 → 省级")
    national = fetch(ROOT)
    if national is None:
        sys.exit("取不到全国数据，检查网络")

    provinces, national_others = [], []
    for feature in national["features"]:
        level = (feature.get("properties") or {}).get("level")
        if level == "province":
            provinces.append(feature)
        else:
            national_others.append(feature)
    print("  省级 {} 个，另有 {} 个非行政要素（如九段线）".format(
        len(provinces), len(national_others)))

    adcodes = []
    for feature in provinces:
        adcode = str((feature.get("properties") or {}).get("adcode", ""))
        if adcode.isdigit():
            adcodes.append(adcode)

    print("二、省级 → 市级 / 区县级（{} 个省级单位）".format(len(adcodes)))
    cities, districts = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for adcode, data in zip(adcodes, pool.map(fetch, adcodes)):
            if data is None:
                continue
            for feature in data["features"]:
                props = feature.get("properties") or {}
                level = props.get("level")
                # 直辖市的下一级直接是 district，没有 city 这一层——
                # 这里不做特殊分支，按 DataV 给的 level 分流即可。
                if str(props.get("adcode", "")) == adcode:
                    continue          # 文件里通常把自己也带上，跳过
                if level == "city":
                    cities.append(feature)
                elif level == "district":
                    districts.append(feature)

    city_adcodes = []
    for feature in cities:
        adcode = str((feature.get("properties") or {}).get("adcode", ""))
        if adcode.isdigit():
            city_adcodes.append(adcode)

    print("三、市级 → 区县级（{} 个市级单位）".format(len(city_adcodes)))
    with concurrent.futures.ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for adcode, data in zip(city_adcodes, pool.map(fetch, city_adcodes)):
            if data is None:
                continue
            for feature in data["features"]:
                props = feature.get("properties") or {}
                if str(props.get("adcode", "")) == adcode:
                    continue
                if props.get("level") == "district":
                    districts.append(feature)

    print("四、写出")
    write("中国_省.geojson", [feature_of(f) for f in provinces + national_others])
    write("中国_市.geojson", [feature_of(f) for f in cities])
    write("中国_县.geojson", [feature_of(f) for f in districts])

    print()
    print("来源：DataV.GeoAtlas（https://datav.aliyun.com/portal/school/atlas/area_selector）")
    print("分发产物时请注明该来源，不要照抄 PRD §6 里的天地图。")


if __name__ == "__main__":
    main()
