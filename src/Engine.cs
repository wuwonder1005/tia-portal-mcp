using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp
{
    public class TiaFailure : Exception
    {
        public TiaFailure(string message) : base(message) { }
    }

    /// <summary>
    /// Every Siemens.Engineering type stays behind this class, so JIT-compiling the MCP
    /// protocol layer never has to resolve Openness assemblies before the resolve hook exists.
    /// </summary>
    public static class Engine
    {
        public static string TiaRoot = @"D:\Program Files\Siemens\Automation\Portal V20";
        private static TiaPortal portal;
        private static Project project;
        private static string connectionMode = "not-connected";

        public static string TempDir
        {
            get
            {
                var d = Path.Combine(Path.GetTempPath(), "tia-mcp");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        }

        // ---------------------------------------------------------------- portal

        private static TiaPortal Portal
        {
            get
            {
                if (portal != null) return portal;
                try
                {
                    foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
                    {
                        if (p.Mode != TiaPortalMode.WithUserInterface) continue;
                        try
                        {
                            portal = p.Attach();
                            connectionMode = "attached-to-gui (pid " + p.Id + ")";
                            Program.Log("attached to running TIA Portal pid " + p.Id);
                            return portal;
                        }
                        catch (Exception ex)
                        {
                            Program.Log("attach to pid " + p.Id + " failed: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("GetProcesses failed: " + ex.Message);
                }

                Program.Log("starting headless TIA Portal...");
                Program.Log("  cwd=" + Environment.CurrentDirectory +
                            " temp=" + Path.GetTempPath() +
                            " user=" + Environment.UserName +
                            " elevated=" + IsElevatedNow());
                try
                {
                    portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                }
                catch (Exception first)
                {
                    // Never kill TIA processes automatically: the user may have unsaved work.
                    // A portal orphaned by a killed server keeps TIA's single-instance lock,
                    // so report it instead of guessing.
                    Program.Log("headless start failed: " + Flatten(first));
                    throw new TiaFailure(
                        "无法启动 TIA Portal 实例：" + Flatten(first) +
                        "。如果博途已经开着（含隐藏实例），请先完全退出，或重启电脑清掉残留进程。");
                }
                connectionMode = "headless (pid " + portal.GetCurrentProcess().Id + ")";
                Program.Log("headless TIA portal ready: " + connectionMode);
                return portal;
            }
        }

        /// <summary>Explicit, user-invoked cleanup of orphaned TIA Portal processes.</summary>
        public static Dictionary<string, object> KillPortals()
        {
            var killed = new List<object>();
            var denied = new List<object>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!p.ProcessName.StartsWith("Siemens.Automation.Portal", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = p.Id;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(8000);
                        killed.Add(id);
                    }
                    catch (Exception ex)
                    {
                        denied.Add(Json.Obj("pid", id, "error", ex.Message));
                    }
                }
                catch { }
            }
            ResetPortal();
            return Json.Obj("ok", denied.Count == 0, "killed", killed, "denied", denied);
        }
        private static void KillStalePortals()
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!p.ProcessName.StartsWith("Siemens.Automation.Portal", StringComparison.OrdinalIgnoreCase)) continue;
                    Program.Log("killing stale portal pid " + p.Id);
                    p.Kill();
                    p.WaitForExit(8000);
                }
                catch (Exception ex)
                {
                    Program.Log("kill portal failed: " + ex.Message);
                }
            }
        }

        private static bool IsElevatedNow()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>Drops a dead portal so the next call reconnects from scratch.</summary>
        public static void ResetPortal()
        {
            try { if (portal != null) portal.Dispose(); }
            catch (Exception ex) { Program.Log("dispose during reset failed: " + ex.Message); }
            portal = null;
            project = null;
            connectionMode = "not-connected";
            Program.Log("portal state reset");
        }

        private static Project Project
        {
            get
            {
                if (project != null) return project;
                foreach (Project p in Portal.Projects)
                {
                    project = p;
                    return project;
                }
                throw new TiaFailure("当前没有打开的项目。请先用 tia_open_project 打开 .ap20，或用 tia_create_project 新建。");
            }
        }

        private static void ClearProject()
        {
            project = null;
            try
            {
                foreach (Project p in Portal.Projects)
                {
                    project = p;
                    return;
                }
            }
            catch { project = null; }
        }

        // ---------------------------------------------------------------- devices

        private static void CollectDevices(IEnumerable<Device> source, List<Device> into)
        {
            foreach (Device d in source) into.Add(d);
        }

        private static List<Device> Devices()
        {
            var list = new List<Device>();
            CollectDevices(Project.Devices, list);
            if (list.Count == 0)
            {
                try { CollectDevices(Project.UngroupedDevicesGroup.Devices, list); }
                catch { }
            }
            return list;
        }

        private static Device FindDevice(string name)
        {
            var all = Devices();
            if (all.Count == 0) throw new TiaFailure("项目里没有任何设备。");
            if (string.IsNullOrEmpty(name))
            {
                foreach (var d in all)
                    if (PlcOf(d) != null) return d;
                return all[0];
            }
            foreach (var d in all)
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
            var names = new List<string>();
            foreach (var d in all) names.Add(d.Name);
            throw new TiaFailure("找不到设备 '" + name + "'。项目里的设备: " + string.Join(", ", names.ToArray()));
        }

        private static PlcSoftware InItem(DeviceItem item)
        {
            try
            {
                var sc = item.GetService<SoftwareContainer>();
                if (sc != null && sc.Software is PlcSoftware) return (PlcSoftware)sc.Software;
            }
            catch { }
            if (item.DeviceItems == null) return null;
            foreach (DeviceItem sub in item.DeviceItems)
            {
                var r = InItem(sub);
                if (r != null) return r;
            }
            return null;
        }

        private static PlcSoftware PlcOf(Device device)
        {
            if (device.DeviceItems == null) return null;
            foreach (DeviceItem item in device.DeviceItems)
            {
                var r = InItem(item);
                if (r != null) return r;
            }
            return null;
        }

        private static PlcSoftware RequirePlc(string deviceName)
        {
            var dev = FindDevice(deviceName);
            var plc = PlcOf(dev);
            if (plc == null) throw new TiaFailure("设备 '" + dev.Name + "' 里没有 PLC 软件（可能是 HMI 或纯硬件设备）。");
            return plc;
        }

        // ----------------------------------------------------------------- tools

        public static Dictionary<string, object> Status()
        {
            var result = Json.Obj();
            result["openness_version"] = typeof(TiaPortal).Assembly.GetName().Version.ToString();
            result["tia_root"] = TiaRoot;

            var processes = new List<object>();
            try
            {
                foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
                    processes.Add(Json.Obj("pid", p.Id, "mode", p.Mode.ToString(),
                        "project", p.ProjectPath == null ? null : p.ProjectPath.FullName));
            }
            catch (Exception ex) { result["process_error"] = ex.Message; }
            result["tia_processes"] = processes;

            try
            {
                result["connection"] = connectionMode;
                result["portal_alive"] = portal != null;
            }
            catch { }

            try
            {
                var prj = Project;
                result["project"] = Json.Obj(
                    "name", prj.Name,
                    "path", prj.Path == null ? null : prj.Path.FullName,
                    "modified", prj.IsModified);
                var devs = new List<object>();
                foreach (var d in Devices())
                    devs.Add(Json.Obj("name", d.Name, "type", d.TypeIdentifier, "has_plc", PlcOf(d) != null));
                result["devices"] = devs;
            }
            catch (Exception ex)
            {
                result["project"] = null;
                result["project_error"] = ex.Message;
            }
            result["ok"] = true;
            return result;
        }

        /// <summary>
        /// Environment diagnostics: which processes this server can actually see.
        /// A sandboxed or cross-session server reports zero TIA processes even while
        /// the user has TIA Portal open, and Openness cannot attach in that state.
        /// </summary>
        public static Dictionary<string, object> Env()
        {
            var result = Json.Obj();
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                result["user"] = id.Name;
                result["session_id"] = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                result["elevated"] = new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                var groups = new List<object>();
                foreach (var g in id.Groups)
                {
                    try
                    {
                        var name = g.Translate(typeof(System.Security.Principal.NTAccount)).Value;
                        if (name.IndexOf("Siemens", StringComparison.OrdinalIgnoreCase) >= 0) groups.Add(name);
                    }
                    catch { }
                }
                result["siemens_groups"] = groups;
            }
            catch (Exception ex) { result["identity_error"] = ex.Message; }

            var visible = new List<object>();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    string name;
                    try { name = p.ProcessName; } catch { continue; }
                    if (name.IndexOf("Siemens", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Portal", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    visible.Add(Json.Obj("pid", p.Id, "name", name, "session", p.SessionId));
                }
            }
            catch (Exception ex) { result["process_enum_error"] = ex.Message; }
            result["visible_siemens_processes"] = visible;
            result["total_processes"] = System.Diagnostics.Process.GetProcesses().Length;

            try
            {
                result["openness_processes"] = TiaPortal.GetProcesses().Count;
            }
            catch (Exception ex) { result["openness_process_error"] = ex.Message; }
            result["ok"] = true;
            return result;
        }

        public static Dictionary<string, object> OpenProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new TiaFailure("path 不能为空。");
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new TiaFailure("项目文件不存在: " + fi.FullName);

            project = Portal.Projects.Open(fi);
            return Json.Obj("ok", true, "name", project.Name, "path", project.Path.FullName);
        }

        /// <summary>
        /// Starts the TIA Portal GUI as a standalone process and waits until Openness can
        /// see it. Headless startup (TiaPortalMode.WithoutUserInterface) fails on some
        /// installations; a visible portal that we then attach to is the reliable path.
        /// </summary>
        public static Dictionary<string, object> LaunchUi(int waitSeconds)
        {
            var exe = Path.Combine(TiaRoot, @"Bin\Siemens.Automation.Portal.exe");
            if (!File.Exists(exe)) throw new TiaFailure("找不到 TIA Portal 可执行文件: " + exe);

            // Already running? Just report it.
            try
            {
                foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
                    return Json.Obj("ok", true, "already_running", true, "pid", p.Id, "mode", p.Mode.ToString(),
                        "project", p.ProjectPath == null ? null : p.ProjectPath.FullName);
            }
            catch (Exception ex) { Program.Log("GetProcesses before launch failed: " + ex.Message); }

            var psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            var proc = Process.Start(psi);
            Program.Log("launched TIA Portal GUI pid=" + (proc == null ? 0 : proc.Id));

            if (waitSeconds < 10) waitSeconds = 10;
            if (waitSeconds > 600) waitSeconds = 600;
            for (int i = 0; i < waitSeconds; i++)
            {
                Thread.Sleep(1000);
                try
                {
                    var list = TiaPortal.GetProcesses();
                    if (list.Count > 0)
                    {
                        var p = list[0];
                        return Json.Obj("ok", true, "already_running", false,
                            "pid", p.Id, "mode", p.Mode.ToString(), "waited_seconds", i + 1,
                            "project", p.ProjectPath == null ? null : p.ProjectPath.FullName);
                    }
                }
                catch (Exception ex) { Program.Log("poll failed: " + ex.Message); }

                if (proc != null && proc.HasExited)
                {
                    throw new TiaFailure("TIA Portal 启动后立即退出（退出码 " + proc.ExitCode +
                        "）。通常是许可、Openness 组权限或安全软件拦截导致，请手动启动博途确认。");
                }
            }
            throw new TiaFailure("已启动 TIA Portal，但 " + waitSeconds + " 秒内 Openness 仍未检测到它。");
        }

        public static Dictionary<string, object> CloseProject(bool save)
        {
            var prj = Project;
            var name = prj.Name;
            if (save) prj.Save();
            prj.Close();
            ClearProject();
            return Json.Obj("ok", true, "closed", name, "saved", save);
        }

        /// <summary>Adds a PLC device from the hardware catalog to the open project.</summary>
        public static Dictionary<string, object> AddPlc(string typeIdentifier, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "PLC_1";
            var typeId = typeIdentifier;
            if (string.IsNullOrWhiteSpace(typeId)) typeId = "OrderNumber:6ES7 516-3AN02-0AB0/V2.0";

            var tried = new List<object>();
            var candidates = new List<string>();
            candidates.Add(typeId);
            if (string.IsNullOrWhiteSpace(typeIdentifier))
            {
                candidates.Add("OrderNumber:6ES7 515-2AM02-0AB0/V2.0");
                candidates.Add("OrderNumber:6ES7 515-2AM01-0AB0/V2.0");
                candidates.Add("OrderNumber:6ES7 1516-3AN02-0AB0/V2.0");
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var dev = Project.Devices.Create(candidate, name);
                    var plc = PlcOf(dev);
                    if (plc != null) return Json.Obj("ok", true, "device", dev.Name, "type", dev.TypeIdentifier, "plc", plc.Name);
                    return Json.Obj("ok", true, "device", dev.Name, "type", dev.TypeIdentifier, "plc", null,
                        "warning", "设备已添加，但没找到 PLC 软件。");
                }
                catch (Exception ex)
                {
                    tried.Add(Json.Obj("type", candidate, "error", Flatten(ex)));
                }
            }
            throw new TiaFailure("添加设备失败，试过的型号都不可用: " + Json.Stringify(tried));
        }

        public static Dictionary<string, object> CreateProject(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new TiaFailure("name 不能为空。");
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) throw new TiaFailure("目录不存在: " + dir.FullName);
            project = Portal.Projects.Create(dir, name);
            return Json.Obj("ok", true, "name", project.Name, "path", project.Path.FullName);
        }

        public static Dictionary<string, object> ListDevices()
        {
            var list = new List<object>();
            foreach (var d in Devices())
            {
                var plc = PlcOf(d);
                var item = Json.Obj("name", d.Name, "type", d.TypeIdentifier, "has_plc", plc != null);
                if (plc != null) item["plc_name"] = plc.Name;
                list.Add(item);
            }
            return Json.Obj("ok", true, "project", Project.Name, "devices", list);
        }

        public static Dictionary<string, object> ListBlocks(string device)
        {
            var plc = RequirePlc(device);
            var blocks = new List<object>();
            WalkBlockGroup(plc.BlockGroup, blocks);
            return Json.Obj("ok", true, "plc", plc.Name, "blocks", blocks);
        }

        private static void WalkBlockGroup(PlcBlockGroup group, List<object> into)
        {
            foreach (PlcBlock b in group.Blocks)
            {
                into.Add(Json.Obj(
                    "name", b.Name,
                    "number", b.Number,
                    "type", b.GetType().Name,
                    "language", b.ProgrammingLanguage.ToString(),
                    "consistent", b.IsConsistent));
            }
            foreach (PlcBlockUserGroup g in group.Groups)
                WalkBlockGroup(g, into);
        }

        public static Dictionary<string, object> ImportScl(string device, string source, bool deleteSource, bool save)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new TiaFailure("source 不能为空（要导入的 SCL 源码）。");
            var plc = RequirePlc(device);
            var name = "mcp_" + Stamp();
            var path = Path.Combine(TempDir, name + ".scl");
            File.WriteAllText(path, source, new UTF8Encoding(true));

            PlcExternalSource src = null;
            try
            {
                src = plc.ExternalSourceGroup.ExternalSources.CreateFromFile(name, path);
                var blocks = src.GenerateBlocksFromSource(GenerateBlockOption.None);
                var names = new List<object>();
                if (blocks != null)
                    foreach (PlcBlock b in blocks) names.Add(b.Name);
                if (deleteSource)
                {
                    try { src.Delete(); } catch (Exception ex) { Program.Log("delete source failed: " + ex.Message); }
                }
                if (save) Project.Save();
                return Json.Obj("ok", true, "generated_blocks", names, "count", names.Count, "plc", plc.Name, "saved", save);
            }
            catch (Exception ex)
            {
                throw new TiaFailure("SCL 导入失败: " + Flatten(ex) + "（源码保留在 " + path + "）");
            }
        }

        public static Dictionary<string, object> DeleteBlock(string device, string blockName)
        {
            var plc = RequirePlc(device);
            var block = FindBlock(plc, blockName);
            if (block == null) throw new TiaFailure("找不到块 '" + blockName + "'。");
            var name = block.Name;
            block.Delete();
            Project.Save();
            return Json.Obj("ok", true, "deleted", name, "plc", plc.Name, "saved", true);
        }
        public static Dictionary<string, object> SaveProject()
        {
            var prj = Project;
            prj.Save();
            return Json.Obj("ok", true, "project", prj.Name, "path", prj.Path == null ? null : prj.Path.FullName);
        }

        public static Dictionary<string, object> ExportBlock(string device, string blockName, string format, string outputPath)
        {
            var plc = RequirePlc(device);
            var block = FindBlock(plc, blockName);
            if (block == null) throw new TiaFailure("找不到块 '" + blockName + "'。");
            var fmt = (format ?? "scl").Trim().ToLowerInvariant();
            var ext = fmt == "xml" ? ".xml" : ".scl";
            var outPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(TempDir, SafeFileName(blockName) + "_" + Stamp() + ext)
                : outputPath;
            var parent = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (fmt == "xml")
            {
                block.Export(new FileInfo(outPath), ExportOptions.None);
            }
            else
            {
                var list = new List<PlcBlock>();
                list.Add(block);
                plc.ExternalSourceGroup.GenerateSource(list, new FileInfo(outPath));
            }
            var text = File.ReadAllText(outPath);
            return Json.Obj("ok", true, "block", blockName, "format", fmt, "path", outPath, "content", text);
        }

        private static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static PlcBlock FindBlock(PlcSoftware plc, string name)
        {
            return FindBlockIn(plc.BlockGroup, name);
        }

        private static PlcBlock FindBlockIn(PlcBlockGroup group, string name)
        {
            foreach (PlcBlock b in group.Blocks)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            foreach (PlcBlockUserGroup g in group.Groups)
            {
                var r = FindBlockIn(g, name);
                if (r != null) return r;
            }
            return null;
        }

        public static Dictionary<string, object> Compile(string device)
        {
            var plc = RequirePlc(device);
            var compilable = GetCompilable(plc);
            if (compilable == null) throw new TiaFailure("该设备不支持编译（找不到编译服务）。");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = compilable.Compile();
            sw.Stop();
            var messages = new List<object>();
            CollectCompileMessages(res.Messages, messages, 0);
            return Json.Obj(
                "ok", res.State != CompilerResultState.Error,
                "state", res.State.ToString(),
                "errors", res.ErrorCount,
                "warnings", res.WarningCount,
                "duration_ms", sw.ElapsedMilliseconds,
                "messages", messages);
        }

        /// <summary>
        /// TIA V20 keeps CompileProvider internal; the supported public surface is the
        /// ICompilable interface returned by the engineering service provider.
        /// </summary>
        private static ICompilable GetCompilable(PlcSoftware plc)
        {
            try
            {
                var direct = plc.GetService<ICompilable>();
                if (direct != null) return direct;
            }
            catch { }

            var asm = typeof(PlcSoftware).Assembly;
            var providerType = asm.GetType("Siemens.Engineering.Compiler.CompileProvider");
            if (providerType == null)
                throw new TiaFailure("无法定位 CompileProvider 类型。");

            System.Reflection.MethodInfo generic = null;
            foreach (var m in typeof(PlcSoftware).GetMethods())
                if (m.Name == "GetService" && m.IsGenericMethodDefinition) { generic = m; break; }
            if (generic == null) throw new TiaFailure("PlcSoftware.GetService<T> 不存在。");

            var service = generic.MakeGenericMethod(providerType).Invoke(plc, null);
            return service as ICompilable;
        }

        private static void CollectCompileMessages(CompilerResultMessageComposition comp, List<object> into, int depth)
        {
            if (comp == null || depth > 6) return;
            foreach (CompilerResultMessage m in comp)
            {
                into.Add(Json.Obj("state", m.State.ToString(), "path", m.Path, "text", m.Description));
                CollectCompileMessages(m.Messages, into, depth + 1);
            }
        }

        public static Dictionary<string, object> ListTagTables(string device)
        {
            var plc = RequirePlc(device);
            var tables = new List<object>();
            WalkTagGroup(plc.TagTableGroup, tables);
            return Json.Obj("ok", true, "plc", plc.Name, "tag_tables", tables);
        }

        private static void WalkTagGroup(PlcTagTableGroup group, List<object> into)
        {
            foreach (PlcTagTable t in group.TagTables)
                into.Add(Json.Obj("name", t.Name, "tags", t.Tags.Count, "is_default", t.IsDefault));
            foreach (PlcTagTableUserGroup g in group.Groups)
                WalkTagGroup(g, into);
        }

        public static Dictionary<string, object> ReadTags(string device, string tableName)
        {
            var plc = RequirePlc(device);
            var table = FindTagTable(plc, tableName);
            if (table == null) throw new TiaFailure("找不到变量表 '" + tableName + "'。");
            var tags = new List<object>();
            foreach (PlcTag t in table.Tags)
            {
                var o = Json.Obj("name", t.Name, "data_type", t.DataTypeName, "address", t.LogicalAddress);
                try
                {
                    var txt = null as string;
                    if (t.Comment != null)
                        foreach (MultilingualTextItem it in t.Comment.Items) { txt = it.Text; break; }
                    o["comment"] = txt;
                }
                catch { }
                tags.Add(o);
            }
            return Json.Obj("ok", true, "table", table.Name, "tags", tags);
        }

        private static PlcTagTable FindTagTable(PlcSoftware plc, string name)
        {
            return FindTagTableIn(plc.TagTableGroup, name);
        }

        private static PlcTagTable FindTagTableIn(PlcTagTableGroup group, string name)
        {
            foreach (PlcTagTable t in group.TagTables)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            foreach (PlcTagTableUserGroup g in group.Groups)
            {
                var r = FindTagTableIn(g, name);
                if (r != null) return r;
            }
            return null;
        }

        public static Dictionary<string, object> UpsertTags(string device, string tableName, List<object> tagDefs, bool createTable, bool save)
        {
            if (tagDefs == null || tagDefs.Count == 0) throw new TiaFailure("tags 不能为空。");
            var plc = RequirePlc(device);
            var name = string.IsNullOrWhiteSpace(tableName) ? "Default tag table" : tableName;

            var table = FindTagTable(plc, name);
            var tableCreated = false;
            if (table == null)
            {
                if (!createTable) throw new TiaFailure("找不到变量表 '" + name + "'，且 create_table 为 false。");
                table = plc.TagTableGroup.TagTables.Create(name);
                tableCreated = true;
            }

            var created = new List<object>();
            var updated = new List<object>();
            var failed = new List<object>();

            foreach (var raw in tagDefs)
            {
                var def = Json.AsObject(raw);
                if (def == null) { failed.Add(Json.Obj("error", "tag entry is not an object")); continue; }
                var tagName = Json.GetString(def, "name");
                var dataType = Json.GetString(def, "data_type");
                if (string.IsNullOrWhiteSpace(dataType)) dataType = Json.GetString(def, "dataType");
                var address = Json.GetString(def, "address");
                var comment = Json.GetString(def, "comment");
                if (string.IsNullOrWhiteSpace(tagName)) { failed.Add(Json.Obj("error", "name missing")); continue; }
                if (string.IsNullOrWhiteSpace(dataType)) dataType = "Bool";

                try
                {
                    PlcTag existing = null;
                    foreach (PlcTag t in table.Tags)
                        if (string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)) { existing = t; break; }

                    if (existing == null)
                    {
                        var tag = string.IsNullOrWhiteSpace(address)
                            ? table.Tags.Create(tagName, dataType, null)
                            : table.Tags.Create(tagName, dataType, address);
                        ApplyComment(tag, comment);
                        created.Add(tagName);
                    }
                    else
                    {
                        existing.DataTypeName = dataType;
                        if (!string.IsNullOrWhiteSpace(address)) existing.LogicalAddress = address;
                        ApplyComment(existing, comment);
                        updated.Add(tagName);
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(Json.Obj("name", tagName, "error", Flatten(ex)));
                }
            }

            if (save && failed.Count == 0) Project.Save();

            return Json.Obj(
                "ok", failed.Count == 0,
                "table", table.Name,
                "table_created", tableCreated,
                "created", created,
                "updated", updated,
                "failed", failed,
                "saved", save);
        }

        private static void ApplyComment(PlcTag tag, string comment)
        {
            if (string.IsNullOrEmpty(comment)) return;
            try
            {
                var text = tag.Comment;
                if (text == null) return;
                foreach (MultilingualTextItem item in text.Items)
                {
                    item.Text = comment;
                    break;
                }
            }
            catch (Exception ex) { Program.Log("set comment failed: " + ex.Message); }
        }

        public static Dictionary<string, object> Download(string device, string mode, bool confirm)
        {
            if (!confirm) throw new TiaFailure("下载会改变真实 PLC 的运行状态。请在请求里显式传 confirm=true 确认后再调用。");
            var dev = FindDevice(device);
            var dp = dev.GetService<DownloadProvider>();
            if (dp == null) throw new TiaFailure("该设备不支持下载（没有 DownloadProvider）。");
            var options = DownloadOptions.Software;
            var m = (mode ?? "software").Trim().ToLowerInvariant();
            if (m == "hardware") options = DownloadOptions.Hardware;
            else if (m == "all") options = DownloadOptions.Hardware | DownloadOptions.Software;
            else if (m == "changes") options = DownloadOptions.SoftwareOnlyChanges;

            var notes = new List<object>();
            DownloadConfigurationDelegate handler = delegate(DownloadConfiguration cfg)
            {
                try { if (cfg.Message != null) notes.Add(cfg.Message); } catch { }
            };

            try
            {
                // The declared property type is ConnectionConfiguration; the live proxy object
                // implements IConfiguration, so the cast is done through object at runtime.
                var configuration = (Siemens.Engineering.Connection.IConfiguration)(object)dp.Configuration;
                var result = dp.Download(configuration, handler, handler, options);
                return Json.Obj(
                    "ok", result.State.ToString() != "Error",
                    "state", result.State.ToString(),
                    "errors", result.ErrorCount,
                    "warnings", result.WarningCount,
                    "configuration_messages", notes);
            }
            catch (Exception ex)
            {
                throw new TiaFailure("下载失败: " + Flatten(ex));
            }
        }

        // ------------------------------------------------------------- selftest

        public static string SelfTest(string directory)
        {
            var sb = new StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                sb.AppendLine("status: " + Json.Stringify(Status()));

                Directory.CreateDirectory(directory);
                sb.AppendLine("project: " + Json.Stringify(CreateProject(directory, "McpSelfTest")));

                var dev = Project.Devices.Create("OrderNumber:6ES7 516-3AN02-0AB0/V2.0", "PLC_1");
                sb.AppendLine("device: " + dev.Name + " / " + dev.TypeIdentifier);

                var plc = PlcOf(dev);
                if (plc == null) throw new TiaFailure("added device has no PLC software");
                sb.AppendLine("plc: " + plc.Name);

                var scl = "FUNCTION_BLOCK \"FB_Motor\"\r\n" +
                          "VAR_INPUT\r\n  Start : Bool;\r\nEND_VAR\r\n" +
                          "VAR_OUTPUT\r\n  Running : Bool;\r\nEND_VAR\r\n" +
                          "BEGIN\r\n  #Running := #Start;\r\nEND_FUNCTION_BLOCK\r\n";
                sb.AppendLine("import_scl: " + Json.Stringify(ImportScl("PLC_1", scl, true, false)));

                var tags = new List<object>();
                tags.Add(Json.Obj("name", "Start_PB", "data_type", "Bool", "address", "%I0.0", "comment", "启动按钮"));
                tags.Add(Json.Obj("name", "Speed_SP", "data_type", "Real", "address", "%MD10", "comment", "速度设定"));
                sb.AppendLine("upsert_tags: " + Json.Stringify(UpsertTags("PLC_1", "IO", tags, true, false)));
                sb.AppendLine("read_tags: " + Json.Stringify(ReadTags("PLC_1", "IO")));

                sb.AppendLine("blocks: " + Json.Stringify(ListBlocks("PLC_1")));
                sb.AppendLine("compile: " + Json.Stringify(Compile("PLC_1")));

                var exported = ExportBlock("PLC_1", "FB_Motor", "scl", null);
                sb.AppendLine("export: " + exported["path"] + " chars=" + Convert.ToString(exported["content"]).Length);

                Project.Save();
                sb.AppendLine("saved. elapsed=" + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
                sb.AppendLine("RESULT: PASS");
            }
            catch (Exception ex)
            {
                sb.AppendLine("FAILED after " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
                sb.AppendLine("  " + Flatten(ex));
                sb.AppendLine("RESULT: FAIL");
            }
            return sb.ToString();
        }

        // --------------------------------------------------------------- helpers

        public static string Flatten(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" -> ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }
    }
}
