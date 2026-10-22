using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace TiaMcp
{
    public static class Program
    {
        private const string DefaultPort = "8756";
        private const string TaskName = "TIA-MCP";

        private static readonly object LogLock = new object();
        private static string logPath;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void Log(string message)
        {
            try
            {
                if (logPath == null)
                    logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tia-mcp.log");
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
                lock (LogLock)
                {
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                    {
                        try { File.Delete(logPath); } catch { }
                    }
                    File.AppendAllText(logPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static int Main(string[] args)
        {
            // A logon task starts with CWD = System32; TIA Portal and Openness dislike that,
            // so always run from the folder that holds the exe.
            // try { Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory; } catch { }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveHandler;
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("UNHANDLED: " + e.ExceptionObject);
            };

            var port = ArgValue(args, "--port") ?? DefaultPort;
            var hidden = HasFlag(args, "--hidden");

            // TIA Portal builds its session workspace under %TEMP%\<guid>\System. After a
            // hard-killed instance those folders can be left behind with stale ACLs, and on
            // this machine the elevated token cannot create anything inside the user's
            // default Temp (security software). Give the portal a temp root next to the exe,
            // which we know the elevated token can write (the log file already lives there).
            try
            {
                var dedicated = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime-temp");
                Directory.CreateDirectory(dedicated);
                Environment.SetEnvironmentVariable("TEMP", dedicated);
                Environment.SetEnvironmentVariable("TMP", dedicated);
                Log("temp redirected to " + dedicated);
            }
            catch (Exception ex)
            {
                Log("temp redirect failed: " + ex.Message);
            }

            if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
            {
                PrintUsage();
                return 0;
            }

            var tiaSource = "n/a";
            var root = TiaLocator.Find(out tiaSource);
            if (root != null) Engine.TiaRoot = root;

            if (HasFlag(args, "--uninstall"))
            {
                if (!EnsureElevated(args)) return 1;
                var code = Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f", false);
                Console.WriteLine(code == 0
                    ? "已移除开机自启任务 '" + TaskName + "'。"
                    : "没有找到自启任务（可能本来就没装）。");
                return 0;
            }

            if (HasFlag(args, "--install"))
            {
                if (!EnsureElevated(args)) return 1;
                return Install(port);
            }

            if (HasFlag(args, "--status"))
            {
                var status = Engine.Status();
                Console.Out.Write(Json.Stringify(status));
                Console.Out.Write(Environment.NewLine);
                return 0;
            }

            if (HasFlag(args, "--selftest"))
            {
                var dir = ArgValue(args, "--selftest") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest");
                var report = Engine.SelfTest(dir);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-report.txt"), report, new UTF8Encoding(false));
                Console.Out.Write(report);
                return report.Contains("RESULT: PASS") ? 0 : 2;
            }

            // Default action: serve over HTTP. Openness only reaches TIA Portal from an
            // elevated client, so make that a hard requirement instead of a silent failure.
            if (!EnsureElevated(args)) return 1;

            if (hidden) ShowWindow(GetConsoleWindow(), 0);

            Console.WriteLine("============================================================");
            Console.WriteLine("  TIA Portal MCP server");
            Console.WriteLine("  endpoint : http://127.0.0.1:" + port + "/mcp");
            Console.WriteLine("  tia      : " + (root ?? "(not found)") + "  [" + tiaSource + "]");
            Console.WriteLine("  rights   : administrator");
            Console.WriteLine("  log      : " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tia-mcp.log"));
            Console.WriteLine("============================================================");
            Console.WriteLine();

            Log("http server starting, pid=" + Process.GetCurrentProcess().Id + " port=" + port + " tia=" + root);
            try
            {
                new HttpMcpServer(int.Parse(port)).Run();
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Console.Error.WriteLine("端口 " + port + " 绑定失败：" + ex.Message);
                Console.Error.WriteLine("可能已经有一个实例在运行。先关掉它，或用 --port 换一个端口。");
                return 3;
            }
            return 0;
        }

        // ------------------------------------------------------------------ install

        private static int Install(string port)
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var user = WindowsIdentity.GetCurrent().Name;

            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" + Environment.NewLine +
                "  <RegistrationInfo><Description>TIA Portal MCP server (http://127.0.0.1:" + port + "/mcp)</Description></RegistrationInfo>" + Environment.NewLine +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>" + Environment.NewLine +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" + Environment.NewLine +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>" + Environment.NewLine +
                "  <Actions Context=\"Author\"><Exec><Command>" + Escape(exe) + "</Command>" +
                "<Arguments>--port " + port + " --hidden</Arguments>" +
                "<WorkingDirectory>" + Escape(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) + "</WorkingDirectory>" +
                "</Exec></Actions>" + Environment.NewLine +
                "</Task>";

            var tmp = Path.Combine(Path.GetTempPath(), "tia-mcp-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);

            var create = Run("schtasks.exe", "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", true);
            try { File.Delete(tmp); } catch { }

            if (create != 0)
            {
                Console.Error.WriteLine("创建计划任务失败（schtasks 返回 " + create + "）。");
                return 4;
            }

            // Replace any manually started instance so two servers never fight over the port.
            // Older builds kept a version suffix in the file name, hence the prefix match.
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    if (!p.ProcessName.StartsWith("TiaMcpServer", StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
            }

            // Tidy leftover builds now that the old instances are gone (their files were locked).
            var self = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(self), "TiaMcpServer*.exe"))
                {
                    if (string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }

            // Wait until the old instance really released the port before starting the task,
            // otherwise the new server exits with "address already in use".
            var portFree = WaitForPortFree(port, 20);
            Log("install: port " + port + " free=" + portFree);

            Run("schtasks.exe", "/run /tn \"" + TaskName + "\"", false);

            // The task now launches the exe directly, so the legacy VBS launcher is dead weight.
            var legacyVbs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "run-hidden.vbs");
            if (File.Exists(legacyVbs))
            {
                try { File.Delete(legacyVbs); } catch { }
            }

            Console.WriteLine("已安装开机自启：任务 '" + TaskName + "'（登录后以管理员权限静默运行）。");
            Console.WriteLine("  程序   : " + exe);
            Console.WriteLine("  端点   : http://127.0.0.1:" + port + "/mcp");
            Console.WriteLine("  卸载   : TiaMcpServer.exe --uninstall");
            return 0;
        }

        // ------------------------------------------------------------------ helpers

        private static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (var a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool EnsureElevated(string[] args)
        {
            if (IsElevated()) return true;

            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var quoted = new StringBuilder();
            foreach (var a in args)
            {
                if (quoted.Length > 0) quoted.Append(' ');
                quoted.Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            }
            if (args.Length == 0) quoted.Append("--port " + DefaultPort);

            try
            {
                Console.WriteLine("需要管理员权限，正在提权…（请在 UAC 对话框点“是”）");
                Process.Start(new ProcessStartInfo(exe, quoted.ToString())
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("提权失败：" + ex.Message);
                Console.Error.WriteLine("请右键以管理员身份运行本程序。");
            }
            return false;
        }

        private static bool WaitForPortFree(string port, int seconds)
        {
            int p;
            if (!int.TryParse(port, out p)) return true;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                    probe.Start();
                    probe.Stop();
                    return true;
                }
                catch
                {
                    System.Threading.Thread.Sleep(500);
                }
            }
            return false;
        }

        private static int Run(string file, string arguments, bool show)
        {
            try
            {
                var psi = new ProcessStartInfo(file, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = !show,
                    RedirectStandardOutput = show,
                    RedirectStandardError = show
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(60000);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log("run failed: " + file + " " + arguments + " -> " + ex.Message);
                return -1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TIA Portal MCP server " + typeof(Program).Assembly.GetName().Version);
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  TiaMcpServer.exe                     以管理员权限启动 HTTP 服务（默认端口 " + DefaultPort + "）");
            Console.WriteLine("  TiaMcpServer.exe --port 9000         指定端口");
            Console.WriteLine("  TiaMcpServer.exe --install           安装开机自启（提权计划任务），并立即启动");
            Console.WriteLine("  TiaMcpServer.exe --uninstall         移除开机自启");
            Console.WriteLine("  TiaMcpServer.exe --status            打印连接状态（JSON）");
            Console.WriteLine("  TiaMcpServer.exe --selftest [目录]    端到端自测（新建演示项目）");
            Console.WriteLine("  TiaMcpServer.exe --help              显示本帮助");
            Console.WriteLine();
            Console.WriteLine("环境要求: Windows + .NET Framework 4.8 + TIA Portal V20（含 Openness），");
            Console.WriteLine("          当前账号需在 \"Siemens TIA Openness\" 组内。");
        }

        private static System.Reflection.Assembly ResolveHandler(object sender, ResolveEventArgs a)
        {
            var simple = new System.Reflection.AssemblyName(a.Name).Name;
            var dirs = new[]
            {
                Path.Combine(Engine.TiaRoot, @"PublicAPI\V20"),
                Path.Combine(Engine.TiaRoot, @"Bin\PublicAPI"),
                Path.Combine(Engine.TiaRoot, @"Bin\PublicAPI\V20"),
                Path.Combine(Engine.TiaRoot, @"Bin")
            };
            foreach (var d in dirs)
            {
                var candidate = Path.Combine(d, simple + ".dll");
                if (File.Exists(candidate))
                {
                    Log("resolve " + simple + " -> " + candidate);
                    return System.Reflection.Assembly.LoadFrom(candidate);
                }
            }
            Log("resolve MISS " + simple);
            return null;
        }
    }
}
