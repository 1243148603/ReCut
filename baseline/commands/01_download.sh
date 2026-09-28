#!/usr/bin/env bash
# 01_download.sh — 下载 Copernicus GLO-30 重庆全域 30 幅分幅
#
# 与教程 §3.1 脚本的差异（重要）：
#   教程的 FILES 数组只列了 28 幅（漏 N29_E107 / N29_E108），因其假设这两幅
#   是「原武隆场景数据」已存在于本机。本机 D:\Map 是全新目录，该假设不成立，
#   照抄会留下一个 2°×1° 的空洞且不报错。故改为双重循环程序化生成 30 个名称。
#
# 用法: bash 01_download.sh
#
# 幂等：脚本可反复重跑。逻辑是「先取远端大小 → 与本地比对 → 一致才跳过」，
# 不一致则走 curl 断点续传。清单文件每跑一次重写，不要并行跑两份。
#
# 注意这里用 set -u（未定义变量报错）而不用 set -e：单幅下载失败不应中断
# 整轮循环，脚本靠 fail 计数器与退出码汇报，调用方据此决定是否重跑。

set -u

DEM_DIR="D:/Map/data/inputs/dem"
MANIFEST="D:/Map/baseline/manifests/dem_downloads.txt"
BASE_URL="https://copernicus-dem-30m.s3.amazonaws.com"

mkdir -p "$DEM_DIR"
: > "$MANIFEST"          # 先清空清单，本次运行结果覆盖上一轮

ok=0; skip=0; fail=0
fail_list=()

for lat in 28 29 30 31 32; do
  for lon in 105 106 107 108 109 110; do
    L="N${lat}"; O="E${lon}"; NAME="${L}_${O}"; F="${DEM_DIR}/${NAME}.tif"
    # S3 上的对象名与目录名同构，沿用官方命名：分幅以整度西南角为基准，
    # N28_E105 覆盖 28°–29°N / 105°–106°E。COG_10 表示 10 倍分级金字塔。
    URL="${BASE_URL}/Copernicus_DSM_COG_10_${L}_00_${O}_00_DEM/Copernicus_DSM_COG_10_${L}_00_${O}_00_DEM.tif"

    # 取远端大小与 ETag（单段上传的裸 MD5，已去掉引号）
    # 这一步是后面「跳过/续传」判断的唯一依据，拿不到就直接判 FAIL 而不盲下。
    H=$(curl -sI --max-time 30 "$URL" 2>/dev/null)
    RL=$(echo "$H" | grep -i '^content-length' | awk '{print $2}' | tr -d '\r')
    ET=$(echo "$H" | grep -i '^etag'           | awk '{print $2}' | tr -d '"\r')

    if [ -z "$RL" ]; then
      echo "[FAIL] ${NAME}: 无法获取远端大小"
      echo "${NAME} remote_len=UNKNOWN status=HEAD_FAILED" >> "$MANIFEST"
      fail=$((fail+1)); fail_list+=("$NAME"); continue
    fi

    # 先校验后跳过：-C - 对已完整文件会让 S3 返回 416，-f 会转成退出码 22
    if [ -f "$F" ]; then
      LS=$(stat -c%s "$F" 2>/dev/null || echo 0)
      if [ "$LS" = "$RL" ]; then
        echo "[跳过] ${NAME}.tif 已完整（$LS 字节）"
        echo "${NAME} remote_len=$RL local_len=$LS etag=$ET status=SKIP_VERIFIED" >> "$MANIFEST"
        skip=$((skip+1)); continue
      fi
      echo "[续传] ${NAME}.tif 本地 $LS / 远端 $RL"
    fi

    echo "[下载] ${NAME}.tif ..."
    # -f 把 HTTP 错误转成非零退出码；-L 跟随重定向；-C - 断点续传；
    # --retry-all-errors 让 5xx 之外的瞬时错误（如 TLS 重置）也重试。
    if curl -fL -C - --retry 5 --retry-delay 3 --retry-all-errors \
            --connect-timeout 20 --max-time 900 \
            -o "$F" "$URL" 2>/dev/null; then
      LS=$(stat -c%s "$F" 2>/dev/null || echo 0)
      if [ "$LS" = "$RL" ]; then
        echo "       完成 $LS 字节 ✓"
        echo "${NAME} remote_len=$RL local_len=$LS etag=$ET status=OK" >> "$MANIFEST"
        ok=$((ok+1))
      else
        echo "       大小不符: 本地 $LS / 远端 $RL"
        echo "${NAME} remote_len=$RL local_len=$LS etag=$ET status=SIZE_MISMATCH" >> "$MANIFEST"
        fail=$((fail+1)); fail_list+=("$NAME")
      fi
    else
      echo "       下载失败"
      echo "${NAME} remote_len=$RL local_len=0 etag=$ET status=CURL_FAILED" >> "$MANIFEST"
      fail=$((fail+1)); fail_list+=("$NAME")
    fi
  done
done

echo ""
echo "=============================================="
echo "下载完成: 新下载 $ok / 已存在跳过 $skip / 失败 $fail （总计 $((ok+skip+fail))，应为 30）"
if [ ${#fail_list[@]} -gt 0 ]; then
  echo "失败清单: ${fail_list[*]}"
  echo "请重跑本脚本（会续传/跳过已完成项）"
fi
echo "清单: $MANIFEST"
echo "=============================================="
# 退出码同时校验「无失败」与「总数正好 30」，防止漏幅却静默通过。
[ "$fail" -eq 0 ] && [ $((ok+skip+fail)) -eq 30 ] && exit 0 || exit 1
