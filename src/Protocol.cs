using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TiaMcp
{
    public class McpServer
    {
        private TextReader input;
        private TextWriter output;

        public void Run()
        {
            var utf8 = new UTF8Encoding(false);
            input = new StreamReader(Console.OpenStandardInput(), utf8, true);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
            stdout.AutoFlush = true;
            output = stdout;

            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                var response = HandleLine(line);
                if (response != null)
                {
                    output.WriteLine(Json.Stringify(response));
                    output.Flush();
                }
            }
            Program.Log("stdin closed, server exiting");
        }

        /// <summary>Handles one JSON-RPC message; returns null for notifications.</summary>
        public object HandleJson(string line)
        {
            return HandleLine(line);
        }

        private object HandleLine(string line)
        {
            object parsed;
            try
            {
                parsed = Json.Parse(line);
            }
            catch (Exception ex)
            {
                Program.Log("bad json: " + ex.Message);
                return Json.Obj("jsonrpc", "2.0", "id", null,
                    "error", Json.Obj("code", -32700, "message", "parse error: " + ex.Message));
            }

            var msg = Json.AsObject(parsed);
            if (msg == null) return null;
            var method = Json.GetString(msg, "method");
            object id;
            msg.TryGetValue("id", out id);
            var isNotification = !msg.ContainsKey("id");

            if (method == null) return null;
            Program.Log("<- " + method);

            try
            {
                object result;
                switch (method)
                {
                    case "initialize":
                        result = InitializeResult(msg);
                        break;
                    case "notifications/initialized":
                    case "notifications/cancelled":
                    case "notifications/roots/list_changed":
                        return null;
                    case "ping":
                        result = Json.Obj();
                        break;
                    case "tools/list":
                        result = Json.Obj("tools", ToolCatalog());
                        break;
                    case "tools/call":
                        result = CallTool(msg);
                        break;
                    case "resources/list":
                        result = Json.Obj("resources", new object[0]);
                        break;
                    case "resources/templates/list":
                        result = Json.Obj("resourceTemplates", new object[0]);
                        break;
                    case "prompts/list":
                        result = Json.Obj("prompts", new object[0]);
                        break;
                    case "logging/setLevel":
                        result = Json.Obj();
                        break;
                    default:
                        return Json.Obj("jsonrpc", "2.0", "id", id,
                            "error", Json.Obj("code", -32601, "message", "method not found: " + method));
                }
                if (isNotification) return null;
                return Json.Obj("jsonrpc", "2.0", "id", id, "result", result);
            }
            catch (TiaFailure ex)
            {
                Program.Log("tool failure: " + ex.Message);
                if (isNotification) return null;
                return ToolError(id, ex.Message);
            }
            catch (Exception ex)
            {
                Program.Log("internal error: " + Engine.Flatten(ex));
                if (isNotification) return null;
                return ToolError(id, "内部错误: " + Engine.Flatten(ex));
            }
        }

        private static object ToolError(object id, string message)
        {
            return Json.Obj("jsonrpc", "2.0", "id", id,
                "result", Json.Obj("content", new object[]
                {
                    Json.Obj("type", "text", "text", message)
                }, "isError", true));
        }

        private static object InitializeResult(Dictionary<string, object> msg)
        {
            string requested = null;
            var ps = Json.AsObject(Get(msg, "params"));
            if (ps != null) requested = Json.GetString(ps, "protocolVersion");

            // Only legacy eras are advertised. Claiming 2026-07-28 would make the client
            // validate every result against the modern wire schemas (ttlMs, cacheScope,
            // server/discover), which this server deliberately does not implement.
            var known = new[] { "2024-11-05", "2025-03-26", "2025-06-18" };
            var picked = "2025-06-18";
            foreach (var v in known)
                if (string.Equals(v, requested, StringComparison.Ordinal)) picked = v;

            return Json.Obj(
                "protocolVersion", picked,
                "capabilities", Json.Obj(
                    "tools", Json.Obj("listChanged", false),
                    "resources", Json.Obj("subscribe", false, "listChanged", false)),
                "serverInfo", Json.Obj("name", "tia-portal", "version", "1.0.0"),
                "instructions",
                    "Siemens TIA Portal V20 bridge over Openness. " +
                    "Workflow: 1) tia_status to see running portals and the open project. " +
                    "2) tia_open_project or tia_create_project. 3) tia_list_devices to find the PLC name. " +
                    "4) tia_import_scl to generate blocks from SCL source, tia_upsert_tags to write tag tables, " +
                    "tia_compile to compile. 5) tia_download only with explicit user confirmation because it changes the real PLC. " +
                    "Block and tag names may contain Chinese characters; pass them exactly as returned by the list tools.");
        }

        private static object Get(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v)) return v;
            return null;
        }

        private static object CallTool(Dictionary<string, object> msg)
        {
            var ps = Json.AsObject(Get(msg, "params"));
            if (ps == null) throw new TiaFailure("缺少 params。");
            var name = Json.GetString(ps, "name");
            var args = Json.AsObject(Get(ps, "arguments"));
            if (args == null) args = new Dictionary<string, object>();

            try
            {
                return Dispatch(name, args);
            }
            catch (Exception ex)
            {
                if (!IsDeadPortal(ex)) throw;
                Program.Log("portal looks dead, resetting and retrying: " + ex.Message);
                Engine.ResetPortal();
                return Dispatch(name, args);
            }
        }

        /// <summary>True when the failure means the TIA instance went away, not a data error.</summary>
        private static bool IsDeadPortal(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? string.Empty;
                if (m.IndexOf("disposed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("stopped running", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("Connection to TiaPortal failed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("RPC server is unavailable", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static object Dispatch(string name, Dictionary<string, object> args)
        {
            Dictionary<string, object> result;
            switch (name)
            {
                case "tia_status":
                    result = Engine.Status();
                    break;
                case "tia_env":
                    result = Engine.Env();
                    break;
                case "tia_launch_ui":
                    result = Engine.LaunchUi(Json.GetBool(args, "wait", true) ? 240 : 15);
                    break;
                case "tia_open_project":
                    result = Engine.OpenProject(Req(args, "path"));
                    break;
                case "tia_close_project":
                    result = Engine.CloseProject(Json.GetBool(args, "save", true));
                    break;
                case "tia_create_project":
                    result = Engine.CreateProject(Req(args, "directory"), Req(args, "name"));
                    break;
                case "tia_add_plc":
                    result = Engine.AddPlc(Json.GetString(args, "type_identifier"), Json.GetString(args, "name"));
                    break;
                case "tia_list_devices":
                    result = Engine.ListDevices();
                    break;
                case "tia_list_blocks":
                    result = Engine.ListBlocks(Json.GetString(args, "device"));
                    break;
                case "tia_import_scl":
                    result = Engine.ImportScl(
                        Json.GetString(args, "device"),
                        Req(args, "source"),
                        Json.GetBool(args, "delete_source", true),
                        Json.GetBool(args, "save", true));
                    break;
                case "tia_export_block":
                    result = Engine.ExportBlock(
                        Json.GetString(args, "device"),
                        Req(args, "block"),
                        Json.GetString(args, "format"),
                        Json.GetString(args, "output_path"));
                    break;
                case "tia_kill_portals":
                    result = Engine.KillPortals();
                    break;
                case "tia_delete_block":
                    result = Engine.DeleteBlock(Json.GetString(args, "device"), Req(args, "block"));
                    break;
                case "tia_save_project":
                    result = Engine.SaveProject();
                    break;
                case "tia_compile":
                    result = Engine.Compile(Json.GetString(args, "device"));
                    break;
                case "tia_list_tag_tables":
                    result = Engine.ListTagTables(Json.GetString(args, "device"));
                    break;
                case "tia_read_tags":
                    result = Engine.ReadTags(Json.GetString(args, "device"), Req(args, "table"));
                    break;
                case "tia_upsert_tags":
                    result = Engine.UpsertTags(
                        Json.GetString(args, "device"),
                        Json.GetString(args, "table"),
                        Json.AsArray(Get(args, "tags")),
                        Json.GetBool(args, "create_table", true),
                        Json.GetBool(args, "save", true));
                    break;
                case "tia_download":
                    result = Engine.Download(
                        Json.GetString(args, "device"),
                        Json.GetString(args, "mode"),
                        Json.GetBool(args, "confirm", false));
                    break;
                default:
                    throw new TiaFailure("未知工具: " + name);
            }

            var failed = result.ContainsKey("ok") && result["ok"] is bool && !(bool)result["ok"];
            return Json.Obj(
                "content", new object[] { Json.Obj("type", "text", "text", Json.Stringify(result)) },
                "isError", failed);
        }

        private static string Req(Dictionary<string, object> args, string key)
        {
            var v = Json.GetString(args, key);
            if (string.IsNullOrWhiteSpace(v)) throw new TiaFailure("缺少必填参数 '" + key + "'。");
            return v;
        }

        private static List<object> ToolCatalog()
        {
            var tools = new List<object>();
            tools.Add(Tool("tia_status",
                "查看 TIA Portal 连接状态、正在运行的博途进程、当前打开的项目和设备清单。无参数。",
                EmptySchema()));
            tools.Add(Tool("tia_env",
                "诊断：本进程能看到的进程、会话、账号与 Siemens 组，用于排查 Openness 连不上博途的原因。无参数。",
                EmptySchema()));
            tools.Add(Tool("tia_launch_ui",
                "启动 TIA Portal 界面（当博途没开时用），并等待 Openness 能检测到它。之后即可用 tia_status / tia_open_project 附着操作。",
                Props(Bool("wait", "是否等待博途完全就绪，默认 true（最多 240 秒）"), false)));
            tools.Add(Tool("tia_open_project",
                "打开已有博途项目 (.ap20)。如果用户已经在博途里打开了该项目，服务器会优先附着到那个实例。",
                Props(Str("path", "项目文件完整路径，例如 D:\\Projects\\Line1\\Line1.ap20"), true)));
            tools.Add(Tool("tia_close_project",
                "关闭当前项目，可选保存。",
                Props(Bool("save", "关闭前是否保存，默认 true"), false)));
            tools.Add(Tool("tia_create_project",
                "在指定目录新建空博途项目。",
                Props(Str("directory", "项目目录"), true, Str("name", "项目名"), true)));
            tools.Add(Tool("tia_add_plc",
                "向当前项目添加一个 PLC 设备（从硬件目录按订货号创建）。不传型号时自动尝试常见 S7-1500 CPU。",
                Props(Str("type_identifier", "订货号，如 OrderNumber:6ES7 516-3AN02-0AB0/V2.0"), false,
                      Str("name", "设备名，默认 PLC_1"), false)));
            tools.Add(Tool("tia_list_devices",
                "列出项目中的设备及其是否带 PLC 软件。",
                EmptySchema()));
            tools.Add(Tool("tia_list_blocks",
                "列出 PLC 里的所有程序块（名称、块号、类型、语言）。",
                Props(Str("device", "PLC 设备名，例如 PLC_1；留空取第一个 PLC"), false)));
            tools.Add(Tool("tia_import_scl",
                "把 SCL 源码导入博途并生成程序块（导入程序的主入口）。支持 FB/FC/OB/DB 与 UDT；同名块会被覆盖。",
                Props(Str("device", "PLC 设备名"), false,
                      Str("source", "SCL 源码全文"), true,
                      Bool("delete_source", "生成后删除临时外部源，默认 true"), false)));
            tools.Add(Tool("tia_export_block",
                "导出单个程序块为 SCL 或 XML 文本，便于读取/修改现有程序。",
                Props(Str("device", "PLC 设备名"), false,
                      Str("block", "块名"), true,
                      Str("format", "scl 或 xml，默认 scl"), false,
                      Str("output_path", "导出文件路径，留空写到临时目录"), false)));
            tools.Add(Tool("tia_kill_portals",
                "清理残留的博途进程（仅在你明确要求时使用）。会关闭所有 Siemens.Automation.Portal 进程，未保存的工程会丢失。",
                EmptySchema()));
            tools.Add(Tool("tia_delete_block",
                "删除 PLC 里的一个程序块并保存项目。",
                Props(Str("device", "PLC 设备名"), false, Str("block", "要删除的块名"), true)));
            tools.Add(Tool("tia_save_project",
                "保存当前项目到磁盘。导入程序和变量表默认会自动保存，需要手动保存时用这个。",
                EmptySchema()));
            tools.Add(Tool("tia_compile",
                "编译 PLC 软件，返回状态、错误/警告数量与详细消息。",
                Props(Str("device", "PLC 设备名"), false)));
            tools.Add(Tool("tia_list_tag_tables",
                "列出变量表及其变量数量。",
                Props(Str("device", "PLC 设备名"), false)));
            tools.Add(Tool("tia_read_tags",
                "读取某个变量表里的全部变量（名称、数据类型、地址、注释）。",
                Props(Str("device", "PLC 设备名"), false, Str("table", "变量表名"), true)));
            tools.Add(Tool("tia_upsert_tags",
                "批量创建或更新变量表中的变量（变量表导入的主入口）。表不存在时自动创建。",
                Json.Obj("type", "object", "properties", Json.Obj(
                    "device", Json.Obj("type", "string", "description", "PLC 设备名"),
                    "table", Json.Obj("type", "string", "description", "变量表名，例如 IO"),
                    "create_table", Json.Obj("type", "boolean", "description", "表不存在时是否创建，默认 true"),
                    "tags", Json.Obj("type", "array", "description", "变量定义数组", "items", Json.Obj(
                        "type", "object",
                        "properties", Json.Obj(
                            "name", Json.Obj("type", "string", "description", "变量名"),
                            "data_type", Json.Obj("type", "string", "description", "数据类型，如 Bool/Int/Real/Time/Array[0..9] of Int/UDT名"),
                            "address", Json.Obj("type", "string", "description", "绝对地址，如 %I0.0 / %MD10 / %Q2.0"),
                            "comment", Json.Obj("type", "string", "description", "注释")),
                        "required", new object[] { "name", "data_type" }))),
                "required", new object[] { "tags" })));
            tools.Add(Tool("tia_download",
                "把项目下载到真实 PLC 并运行。危险操作：会停机并改变现场设备状态；必须由用户显式确认后才可调用。",
                Props(Str("device", "PLC 设备名"), false,
                      Str("mode", "software（仅程序）/ hardware（仅硬件）/ all / changes，默认 software"), false,
                      Bool("confirm", "必须传 true 才会执行"), true)));
            return tools;
        }

        private static object Tool(string name, string description, object schema)
        {
            return Json.Obj("name", name, "description", description, "inputSchema", schema);
        }

        private static object EmptySchema()
        {
            return Json.Obj("type", "object", "properties", Json.Obj());
        }

        private static object Props(params object[] pairs)
        {
            var props = Json.Obj();
            var required = new List<object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                var pair = pairs[i] as object[];
                if (pair == null) continue;
                var name = Convert.ToString(pair[0]);
                props[name] = pair[1];
                if (pairs[i + 1] is bool && (bool)pairs[i + 1]) required.Add(name);
            }
            var schema = Json.Obj("type", "object", "properties", props);
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        private static object Str(string name, string description)
        {
            return new object[] { name, Json.Obj("type", "string", "description", description) };
        }

        private static object Bool(string name, string description)
        {
            return new object[] { name, Json.Obj("type", "boolean", "description", description) };
        }
    }
}
