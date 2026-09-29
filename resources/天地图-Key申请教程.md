# 天地图 API Key 申请教程

> 目的：拿到一个**服务器端**的天地图 Key，供 `fetch_boundaries.py` 从天地图
> 政区要素服务抓取全国省/市/县边界。
>
> 本文里标注"已实测"的地方，都是在本机实际调用天地图接口验证过的，
> 不是照抄官网说明。

---

## 0. 先说结论：Key 分两种，我们要的是「服务器端」

天地图的 Key 按**应用类型**分两种，创建的时侯选定，**权限互不通用**：

| 应用类型 | 用途 | 校验方式 |
|---|---|---|
| **浏览器端** | 网页里的 JS API（地图显示、Web 服务） | 校验请求的 `Referer` |
| **服务器端** | 后端服务、脚本、桌面程序 | 可配 IP 白名单，不校验 Referer |

**脚本必须用服务器端。**

### 已实测：浏览器端 Key 拿去跑脚本会怎样

用 `7e1f…` 这个 Key 调 `https://api.tianditu.gov.cn/v2/administrative`，返回：

```json
{"code": 301013, "msg": "权限类型错误",
 "resolve": "Key权限类型为:浏览器端，请使用浏览器端访问"}
```

错误码 **301013 = 权限类型错误**，提示写得很直白。看到这个码就是 Key 类型不对，
不用去查网络、参数、白名单——那些都没问题。

---

## 1. 注册与实名认证

1. 打开 <https://console.tianditu.gov.cn/>
2. 注册账号（手机号 + 验证码）
3. **完成实名认证**——个人或企业均可。未实名的账号创建不了应用。

> 认证要审核，个人认证通常当天可过。这一步是唯一需要等的环节。

---

## 2. 创建应用

1. 登录后进 <https://console.tianditu.gov.cn/api/key>
2. 点「创建新应用」，按下表填写：

| 字段 | 填什么 | 说明 |
|---|---|---|
| 应用名称 | `RECUT`（随意） | 只是个标签 |
| 行业类别 | 随意，如「低空经济」 | 不影响权限 |
| **应用类型** | **`服务器端`** | ← **最关键的一项**，选错了就等于白建 |
| IP 白名单 | **留空** | 留空 = 不限制来源 IP |

关于 IP 白名单：官方说明是"输入 IP 白名单后，只有白名单中的 IP 能够访问应用，
不输入任何信息表示无 IP 限制"。留空的代价是 Key 泄露后别人也能用（消耗你的配额）；
填上则更安全，但**换网络、换机器就要回来改**。

3. 勾选《天地图服务条款》，点「提交」

---

## 3. 复制 Key

提交后在应用列表里能看到该应用的 **Key**，形如：

```
7e1f29296f533370f6a39dabf59fac15     ← 32 位十六进制
```

这就是 `tk` 参数的值。

> ⚠️ 天地图的应用类型**创建后不能改**。如果现有的 Key 是浏览器端，
> 需要**新建一个服务器端应用**，而不是改旧的。

---

## 3b. 另一条路：给浏览器端应用开「安全密钥」（sk）

天地图对**浏览器端**应用提供一种叫**安全密钥**（`sk`）的加倍防护，官方说明是
"避免应用 key 泄漏的情况下 key 被盗用"，用途是**经代理服务器转发**时补上授权。

官方给的 nginx 写法（大意）：

```nginx
location /custom/search {
    set $args "$args&sk=您的安全密钥";
    proxy_pass http://api.tianditu.gov.cn/v2/search;
}
```

也就是说：代理把请求转发给天地图时，附上 `&sk=…`，天地图就认。

**这条路也能走通我们的场景**（本机脚本 = 那个代理的位置）：

1. 进控制台 → 应用设置 → **打开「安全密钥」开关**
2. 复制生成的安全密钥
3. 调用时带上两个参数：`tk=应用key` **和** `sk=安全密钥`

```bash
export TIANDITU_KEY=7e1f29296f533370f6a39dabf59fac15
export TIANDITU_SK=你的安全密钥
python resources/fetch_boundaries.py --source tianditu
```

### 两条路怎么选

| | 服务器端 Key | 浏览器端 Key + sk |
|---|---|---|
| 步骤 | 新建一个应用（类型选服务器端） | 在现有应用里打开开关 |
| 调用参数 | 只要 `tk` | 要 `tk` + `sk` |
| 适用 | 后端/脚本/桌面程序，**语义最正** | 浏览器端应用经代理转发 |
| 风险 | 无 | 用浏览器端凭据做服务端调用，与官方对该密钥的定位（前端防泄漏）不完全一致；**配额与条款按浏览器端那一套算** |

**推荐第一条**：新建一个服务器端应用，只多花一分钟，语义和条款都干净。
第二条能用，但相当于把前端凭据拿到后端用——真被查用量时不好解释。

两条都不必二选一：**申请一个服务器端 Key 是最省事的**，`sk` 那条留着备用。

---

## 4. 把 Key 交给本机脚本（不要提交进仓库）

`fetch_boundaries.py` 按下面的顺序找 Key，**三种任选一种**：

**方式一：环境变量（推荐，最简单）**

```bash
export TIANDITU_KEY=你的32位key
python resources/fetch_boundaries.py --source tianditu
```

Windows cmd：

```cmd
set TIANDITU_KEY=你的32位key
python resources\fetch_boundaries.py --source tianditu
```

**方式二：本机配置文件**

把 Key 写进 `resources/.tianditu_key`（**已在 .gitignore 里**，不会进仓库）：

```
7e1f29296f533370f6a39dabf59fac15
```

**方式三：命令行参数**

```bash
python resources/fetch_boundaries.py --source tianditu --key 你的key
```

> Key 是凭据。**不要**写进 `fetch_boundaries.py`、README 或任何会提交的文件里——
> 提交上去之后再删，历史里仍然留着。

---

## 5. 申请完之后

把 Key 用上面任一方式放好，告诉我一声，我来：
1. 先用一个最小的请求验证 Key 类型正确（避免又跑到一半才发现问题）
2. 抓取全国省/市/县三级边界（约 3200 次调用）
3. 跑一遍完整验收（加载、过滤、层级、真实裁剪）
4. 把 `THIRD-PARTY-NOTICES.txt` 与 README 的来源署名改回**天地图**

---

## 当前结论（2026-09-29 实测，未打通）

Key 申请下来了（应用 RECUT，控制台显示「服务器端」，Key `87c6…`），
但**天地图网关不认这个类型**：

```
GET https://api.tianditu.gov.cn/v2/administrative?...&tk=87c6…
→ 403 {"code":301013,"msg":"权限类型错误",
       "resolve":"Key权限类型为:浏览器端，请使用浏览器端访问"}
```

已排除的可能：

| 排除项 | 怎么排的 |
|---|---|
| 请求格式 | GET / POST 表单 / POST JSON 三种结果完全一致 |
| 下发延迟 | 隔 45 秒重试 5 次（约 3 分钟）仍同 |
| 接口写错 | 同一 Key 在 geocoder、v2/search、v2/administrative 上都是 301013 |
| Key 无效 | 无效 Key 返回的是 `301001 不是key`，不是 301013 |
| User-Agent | 自定义 UA 会被打成 500；换成浏览器 UA 后才是规范的 301013 |

**没排除的**：IP 白名单留空、账号开发者类型（个人/企业）、天地图侧的
配置下发。要接着试就从这三项入手，尤其是**先填上 IP 白名单**再重试。

当前项目**继续使用 DataV.GeoAtlas**（数据已抓好并验收通过）。
脚本的 `--source tianditu` 路径保留，将来 Key 可用了直接抓一遍即可换源，
不用改代码。

---

## 附：接口形态（已从官方文档核实）

```
GET http://api.tianditu.gov.cn/v2/administrative?postStr={...}&tk=KEY
```

`postStr` 是一个 JSON 字符串，字段：

| 字段 | 类型 | 说明 | 默认 |
|---|---|---|---|
| `searchWord` | string | 查询关键字（行政区名称或代码） | — |
| `searchType` | string | `0`=按 code 查询，`1`=按名称查询 | `0` |
| `needSubInfo` | boolean | 是否需要下一级信息 | `false` |
| `needAll` | boolean | 是否需要所有子节点 | `false` |
| `needPolygon` | boolean | 是否需要行政区域范围 | `false` |
| `needPre` | boolean | 是否需要上一级信息 | `false` |

返回：

| 字段 | 说明 |
|---|---|
| `returncode` | `100` 成功，`101` 没有查到结果 |
| `data[]` | 行政区数组 |
| `data[].name` | 行政区名称 |
| `data[].cityCode` | 行政区划代码 |
| `data[].level` | 行政级别 |
| `data[].adminType` | 行政区类型（省/市/县） |
| `data[].bound` | 四角点坐标（外包框） |
| `data[].points` | **行政区域范围**（边界多边形，抓的就是它） |
| `data[].parents` | 上级行政区信息 |

> 官方文档标注旧版 `http://api.tianditu.gov.cn/administrative`（V1.0）
> 已于 **2024-06-30 下线**，请用 `v2`。V2 的参数与返回结构以官网
> <https://cloudcenter.tianditu.gov.cn/administrativeDivision> 为准
> （该页是 JS 单页应用，脚本抓不到正文，需要浏览器打开看）。

**配额**：天地图对每类服务有日调用量限制。抓全国三级约需 3200 次调用，
远低于常见配额，但具体数字请在你控制台的用量页确认——这个我没有账号，无法核实。

---

## 附：如果天地图走不通

仓库里现在用的是 **DataV.GeoAtlas**（免密公开接口），数据已经抓好并验收通过
（省 34 / 市 363 / 县 2840）。天地图不是"必须"，走它的意义在于：

- 与 PRD §6 记录的数据源一致（署名口径统一）
- 天地图是国家地理信息公共服务平台，作为交付产物的数据来源更正式

如果申请不下来，继续用 DataV 也完全可用，只是署名要按"来源：DataV.GeoAtlas"写
（现在的 `THIRD-PARTY-NOTICES.txt` 就是这么写的）。
