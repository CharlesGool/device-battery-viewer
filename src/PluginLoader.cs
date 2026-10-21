using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;

namespace BatteryViewer
{
    public class LoadedPlugin
    {
        public string Id;
        public string Path;          // 内置插件为空
        public BatteryPlugin Plugin;
        public string Error = "";    // 加载失败的原因
        public bool Disabled;
        public bool Builtin;
    }

    public class PluginFileException : Exception
    {
        public PluginFileException(string message) : base(message) { }
    }

    /// <summary>
    /// 插件加载: 内置插件编译在 exe 里; 用户插件放在 %APPDATA%\DeviceBatteryViewer\plugins\,
    /// 可以是 .cs 源文件(加载时用系统自带的 C# 编译器编译)或编译好的 .dll。文件名以 _ 开头的不加载。
    /// 用户插件和内置插件同 id 时覆盖内置的。
    /// </summary>
    public static class PluginLoader
    {
        public static List<LoadedPlugin> LoadAll(ICollection<string> disabled)
        {
            var found = new Dictionary<string, LoadedPlugin>();
            Add(found, new LoadedPlugin { Id = "logitech_hidpp", Plugin = new Plugins.LogitechHidpp(), Builtin = true });
            Add(found, new LoadedPlugin { Id = "bluetooth", Plugin = new Plugins.Bluetooth(), Builtin = true });
            if (Directory.Exists(Settings.PluginDir))
            {
                var files = Directory.GetFiles(Settings.PluginDir, "*.cs").Concat(Directory.GetFiles(Settings.PluginDir, "*.dll"));
                foreach (var path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.Path.GetFileName(path).StartsWith("_")) continue;
                    foreach (var lp in LoadFile(path)) Add(found, lp);
                }
            }
            foreach (var lp in found.Values)
                lp.Disabled = disabled.Contains(lp.Id);
            return found.Values.ToList();
        }

        private static void Add(Dictionary<string, LoadedPlugin> found, LoadedPlugin lp)
        {
            LoadedPlugin old;
            if (found.TryGetValue(lp.Id, out old) && old.Plugin != null) old.Plugin.Dispose();
            if (lp.Plugin != null) lp.Plugin.Id = lp.Id;
            found[lp.Id] = lp;
        }

        /// <summary>加载一个插件文件(.cs 或 .dll), 返回其中每个插件类; 失败时返回一条带 Error 的记录。</summary>
        public static List<LoadedPlugin> LoadFile(string path)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            Assembly asm;
            try
            {
                asm = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? Assembly.Load(File.ReadAllBytes(path))    // 读成字节再加载, 不锁文件, 方便替换
                    : Compile(path);
            }
            catch (Exception e)
            {
                return new List<LoadedPlugin> { new LoadedPlugin { Id = stem, Path = path, Error = e.Message } };
            }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            var classes = types.Where(t => typeof(BatteryPlugin).IsAssignableFrom(t) && !t.IsAbstract).ToList();
            if (classes.Count == 0)
                return new List<LoadedPlugin> { new LoadedPlugin { Id = stem, Path = path, Error = "文件里没有找到继承 BatteryPlugin 的类" } };

            var result = new List<LoadedPlugin>();
            foreach (var t in classes)
            {
                var lp = new LoadedPlugin { Id = classes.Count == 1 ? stem : stem + "." + t.Name, Path = path };
                try { lp.Plugin = (BatteryPlugin)Activator.CreateInstance(t); }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    lp.Error = "创建插件失败: " + inner;
                }
                result.Add(lp);
            }
            return result;
        }

        /// <summary>
        /// 用 .NET Framework 自带的 C# 编译器在内存里编译插件源文件。
        /// 默认引用 System / System.Core / System.Drawing / 本程序; 需要别的程序集在文件里写一行 "// ref: xxx.dll"。
        /// </summary>
        private static Assembly Compile(string path)
        {
            string source = File.ReadAllText(path, Encoding.UTF8);
            var p = new CompilerParameters { GenerateInMemory = true, GenerateExecutable = false, TreatWarningsAsErrors = false };
            p.ReferencedAssemblies.AddRange(new[] { "System.dll", "System.Core.dll", "System.Drawing.dll", typeof(BatteryPlugin).Assembly.Location });
            foreach (Match m in Regex.Matches(source, @"^\s*//\s*ref:\s*(\S+)", RegexOptions.Multiline))
                p.ReferencedAssemblies.Add(m.Groups[1].Value);
            using (var provider = new CSharpCodeProvider(new Dictionary<string, string> { { "CompilerVersion", "v4.0" } }))
            {
                var r = provider.CompileAssemblyFromFile(p, path);
                var errors = r.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToList();
                if (errors.Count > 0)
                    throw new PluginFileException("编译失败:\r\n" + string.Join("\r\n",
                        errors.Select(e => string.Format("第 {0} 行: {1} ({2})", e.Line, e.ErrorText, e.ErrorNumber))));
                return r.CompiledAssembly;
            }
        }

        /// <summary>插件文件复制到插件目录后的路径(去掉开头的 _, 否则不会被加载)。</summary>
        public static string TargetPath(string src)
        {
            string name = System.IO.Path.GetFileName(src).TrimStart('_');
            return System.IO.Path.Combine(Settings.PluginDir, name.Length > 0 ? name : "plugin.cs");
        }

        /// <summary>检查 src 是不是能用的插件, 能用就复制到插件目录; 不能用抛 PluginFileException。</summary>
        public static List<LoadedPlugin> Install(string src)
        {
            string ext = System.IO.Path.GetExtension(src).ToLowerInvariant();
            if (ext != ".cs" && ext != ".dll") throw new PluginFileException("插件是一个 .cs 源文件或 .dll 文件");
            var checkedList = LoadFile(src);
            var bad = checkedList.FirstOrDefault(lp => lp.Error.Length > 0);
            if (bad != null) throw new PluginFileException(bad.Error);
            foreach (var lp in checkedList) lp.Plugin.Dispose();
            Directory.CreateDirectory(Settings.PluginDir);
            File.Copy(src, TargetPath(src), true);
            return checkedList;
        }

        /// <summary>按模板在插件目录里建一个新插件文件, 返回路径。name 只能是英文、数字、下划线。</summary>
        public static string NewFromTemplate(string name)
        {
            if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$"))
                throw new PluginFileException("名字只能用英文字母、数字和下划线, 并以字母开头");
            string dest = System.IO.Path.Combine(Settings.PluginDir, name + ".cs");
            if (File.Exists(dest)) throw new PluginFileException(System.IO.Path.GetFileName(dest) + " 已经存在");
            string cls = string.Concat(name.Split('_').Where(s => s.Length > 0).Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));
            string text = Template().Replace("ExampleMouse", cls).Replace("\"示例鼠标\"", "\"" + name + "\"");
            Directory.CreateDirectory(Settings.PluginDir);
            File.WriteAllText(dest, text, new UTF8Encoding(true));
            return dest;
        }

        public static string Template()
        {
            using (var s = typeof(PluginLoader).Assembly.GetManifestResourceStream("PluginTemplate.cs"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }
    }
}
