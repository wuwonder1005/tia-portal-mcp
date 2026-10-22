using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace TiaMcp
{
    internal static class Json
    {
        private static readonly JavaScriptSerializer Ser = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }

        public static object Parse(string text)
        {
            return Ser.DeserializeObject(text);
        }

        public static string Stringify(object value)
        {
            return Ser.Serialize(value);
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            var arr = value as object[];
            if (arr != null) return new List<object>(arr);
            var list = value as ArrayList;
            if (list != null) return new List<object>(list.ToArray());
            var l = value as List<object>;
            return l;
        }

        public static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object raw;
            if (!obj.TryGetValue(key, out raw) || raw == null) return null;
            return raw as string ?? Convert.ToString(raw);
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            if (obj == null) return fallback;
            object raw;
            if (!obj.TryGetValue(key, out raw) || raw == null) return fallback;
            if (raw is bool) return (bool)raw;
            bool parsed;
            return bool.TryParse(Convert.ToString(raw), out parsed) ? parsed : fallback;
        }

        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[Convert.ToString(kv[i])] = kv[i + 1];
            return d;
        }
    }
}
