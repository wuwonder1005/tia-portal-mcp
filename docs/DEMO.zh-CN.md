# 演示：一句话导入程序 + 变量表 + 编译

下面是一次真实会话的记录（2026-10-22，在 `FB_StartStop_项目` 上验证），展示用自然语言操作博途的完整流程。

## 环境

- TIA Portal V20 + Openness，`PLC_1`（S7-1500）
- 服务器已用管理员身份运行在 `http://127.0.0.1:8756/mcp`

## 第 1 步：查看状态

> 我：博途状态如何？

服务器返回：

```json
{
  "connection": "headless (pid 17052)",
  "portal_alive": true,
  "project": { "name": "FB_StartStop_项目" },
  "devices": [ { "name": "PLC_1", "has_plc": true } ]
}
```

## 第 2 步：打开项目（中文路径也能用）

> 我：打开 C:\Users\weidong.wu\Desktop\FB_StartStop_项目\FB_StartStop_项目.ap20

```json
{ "ok": true, "name": "FB_StartStop_项目" }
```

## 第 3 步：导入一个 SCL 功能块

> 我：把这个 SCL 导入 PLC_1

```pascal
FUNCTION_BLOCK "FB_McpDemo"
{ S7_Optimized_Access := 'TRUE' }
   VAR_INPUT  Start : Bool; END_VAR
   VAR_OUTPUT Motor : Bool; END_VAR
BEGIN
   #Motor := #Start;
END_FUNCTION_BLOCK
```

服务器结果：

```json
{ "ok": true, "generated_blocks": ["FB_McpDemo"], "saved": true }
```

## 第 4 步：批量导入变量表

> 我：给 IO 表加这几个变量

```json
{ "table": "MCP_IO", "tags": [
  { "name": "Demo_Start", "data_type": "Bool", "address": "%I0.0", "comment": "演示-启动按钮" },
  { "name": "Demo_Motor", "data_type": "Bool", "address": "%Q0.0", "comment": "演示-电机输出" }
]}
```

服务器结果：

```json
{ "ok": true, "table_created": true, "created": ["Demo_Start", "Demo_Motor"] }
```

## 第 5 步：编译

> 我：编译一下

```json
{ "state": "Error", "errors": 1, "messages": [
  { "state": "Success", "text": "Block was successfully compiled." },
  { "state": "Error", "path": "Main (OB1)", "text": "The interface of the standard OB is smaller than the minimum value of 20 bytes." }
]}
```

——新导入的块编译成功；报错来自项目里原有的 `Main (OB1)`。

## 第 6 步：导出回读

> 我：把 FB_McpDemo 导成 SCL 看看

```json
{ "ok": true, "format": "scl",
  "content": "FUNCTION_BLOCK \"FB_McpDemo\"\r\n ... // 中文注释完整还原 ..." }
```

## 一句话总结

```
打开项目 → 导入 SCL → 写变量表 → 编译 → 导出
```

全程不用碰博途界面，只需要在对话里说需求。
