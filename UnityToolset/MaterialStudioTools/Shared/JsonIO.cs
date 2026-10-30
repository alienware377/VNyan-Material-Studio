using System;

namespace MaterialStudio
{
    /// <summary>
    /// JSON for the files the plugin and the editor toolset exchange.
    ///
    /// Editor (Unity 2019.4): JsonUtility. Plugin (MS_RUNTIME): Newtonsoft, because VNyan loads
    /// plugin assemblies from bytes and Unity's own serializer then does not know the plugin's
    /// classes: JsonUtility silently returned an EMPTY list of edits (measured in VNyan 1.7.1b).
    /// Newtonsoft is set to public instance FIELDS only, which writes exactly what JsonUtility
    /// writes (Color as r/g/b/a, Vector4 as x/y/z/w), so both sides read each other's files.
    /// </summary>
    public static class JsonIO
    {
#if MS_RUNTIME
        sealed class FieldsOnly : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override System.Collections.Generic.List<System.Reflection.MemberInfo> GetSerializableMembers(Type objectType)
            {
                var l = new System.Collections.Generic.List<System.Reflection.MemberInfo>();
                foreach (var f in objectType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    if (!f.IsInitOnly || objectType.IsValueType) l.Add(f);
                return l;
            }

            protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(System.Reflection.MemberInfo member, Newtonsoft.Json.MemberSerialization ms)
            {
                var p = base.CreateProperty(member, ms);
                p.Readable = true;
                p.Writable = true;
                return p;
            }
        }

        static readonly Newtonsoft.Json.JsonSerializerSettings settings = new Newtonsoft.Json.JsonSerializerSettings
        {
            ContractResolver = new FieldsOnly(),
            Formatting = Newtonsoft.Json.Formatting.None,
            ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace,
            MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore,
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
            ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
        };

        public static string ToJson(object o, bool pretty = false)
        {
            var s = new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = settings.ContractResolver, ObjectCreationHandling = settings.ObjectCreationHandling,
                MissingMemberHandling = settings.MissingMemberHandling, ReferenceLoopHandling = settings.ReferenceLoopHandling,
                Formatting = pretty ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None,
            };
            return Newtonsoft.Json.JsonConvert.SerializeObject(o, s);
        }

        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json, settings);
        }
#else
        public static string ToJson(object o, bool pretty = false) { return UnityEngine.JsonUtility.ToJson(o, pretty); }

        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            return UnityEngine.JsonUtility.FromJson<T>(json);
        }
#endif
    }
}
