using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TiaMcp
{
    /// <summary>
    /// Locates the TIA Portal installation on any machine, so the binary is not tied to
    /// one hard-coded drive. Order: Openness registry -> TiaPortalLocation env var ->
    /// a few well-known install roots.
    /// </summary>
    public static class TiaLocator
    {
        public static string Find(out string source)
        {
            var fromRegistry = FromRegistry();
            if (fromRegistry != null) { source = "registry"; return fromRegistry; }

            var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, @"PublicAPI\V20\Siemens.Engineering.dll")))
            {
                source = "TiaPortalLocation";
                return env.TrimEnd('\\');
            }

            var roots = new List<string>
            {
                @"C:\Program Files\Siemens\Automation",
                @"C:\Program Files (x86)\Siemens\Automation",
                @"D:\Program Files\Siemens\Automation",
                @"D:\Program Files (x86)\Siemens\Automation",
                @"E:\Program Files\Siemens\Automation"
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var portal in Directory.GetDirectories(root, "Portal V*").OrderByDescending(x => x))
                {
                    if (File.Exists(Path.Combine(portal, @"PublicAPI\V20\Siemens.Engineering.dll")))
                    {
                        source = "filesystem";
                        return portal;
                    }
                }
            }

            source = "not found";
            return null;
        }

        private static string FromRegistry()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var openness = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness"))
                    {
                        if (openness == null) continue;
                        foreach (var versionKey in openness.GetSubKeyNames().OrderByDescending(x => x))
                        {
                            using (var api = openness.OpenSubKey(versionKey + @"\PublicAPI"))
                            {
                                if (api == null) continue;
                                foreach (var apiVersion in api.GetSubKeyNames().OrderByDescending(x => x))
                                {
                                    using (var entry = api.OpenSubKey(apiVersion))
                                    {
                                        if (entry == null) continue;
                                        var dll = entry.GetValue("Siemens.Engineering") as string;
                                        if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) continue;

                                        // ...\Portal V20\PublicAPI\V20\Siemens.Engineering.dll -> ...\Portal V20
                                        var dir = Path.GetDirectoryName(dll);          // PublicAPI\V20
                                        var publicApi = Path.GetDirectoryName(dir);    // PublicAPI
                                        var portal = Path.GetDirectoryName(publicApi);
                                        if (portal != null && Directory.Exists(Path.Combine(portal, "Bin"))) return portal;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("registry lookup failed (" + view + "): " + ex.Message);
                }
            }
            return null;
        }
    }
}
