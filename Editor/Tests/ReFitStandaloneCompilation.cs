using System;
using System.IO;
using System.Linq;
using UnityEditor.Compilation;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitStandaloneCompilation
    {
        private static AssemblyBuilder activeBuild;

        /// <summary>Compiles ReFit without any Orbiters or project assemblies. Results are written under Temp.</summary>
        public static void Run()
        {
            if (activeBuild != null) throw new InvalidOperationException("Standalone compilation is already running.");
            string root = Path.GetFullPath("Packages/orbiters.refit");
            string output = Path.GetFullPath("Temp/ReFitTests/standalone");
            Directory.CreateDirectory(output);
            var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/Integrations/XRay/"))
                .ToArray();
            var builder = new AssemblyBuilder(Path.Combine(output, "ReFitStandalone.dll"), sources)
            {
                flags = AssemblyBuilderFlags.EditorAssembly,
                additionalReferences = Directory.GetFiles(Path.GetDirectoryName(typeof(UnityEditor.EditorWindow).Assembly.Location), "*.dll")
            };
            builder.excludeReferences = builder.defaultReferences.Where(p =>
                Path.GetFileNameWithoutExtension(p).StartsWith("orbiters.", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(p).StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)).ToArray();
            File.WriteAllLines(Path.Combine(output, "excluded-references.txt"), builder.excludeReferences);
            File.WriteAllText(Path.Combine(output, "result.txt"), "RUNNING");
            builder.buildFinished += (path, messages) =>
            {
                var errors = messages.Where(m => m.type == CompilerMessageType.Error).ToArray();
                File.WriteAllLines(Path.Combine(output, "result.txt"), new[] { errors.Length == 0 ? "PASS" : "FAIL" }
                    .Concat(errors.Select(m => m.file + ":" + m.line + " " + m.message)));
                Debug.Log("[ReFit Tests] Standalone compilation: " + errors.Length + " errors.");
                activeBuild = null;
            };
            activeBuild = builder;
            if (!builder.Build()) { activeBuild = null; throw new InvalidOperationException("Unity could not start standalone compilation."); }
        }
    }
}
