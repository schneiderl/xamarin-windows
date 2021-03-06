// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Xamarin.Windows.Tasks
{
	public class GenerateBundledAssemblies : Task
	{

		[Required]
		public ITaskItem[] Assemblies { get; set; }

		[Required]
		public ITaskItem[] ConfigFiles { get; set; }

		[Required]
		public string OutputDirectory { get; set; }

		public bool SkipUnchanged { get; set; }

		[Output]
		public ITaskItem[] GeneratedFiles { get; set; }

		[Output]
		public ITaskItem[] BundledConfigFiles { get; set; }

		public override bool Execute()
		{
			try {
				return DoExecute();
			} catch (Exception e) {
				Log.LogError("{0}", e);
				return false;
			}
		}

		private bool DoExecute()
		{
			Log.LogDebugMessage("GenerateBundledAssemblies Task");
			Log.LogDebugTaskItems("  Assemblies:", Assemblies);
			Log.LogDebugTaskItems("  ConfigFiles:", ConfigFiles);
			Log.LogDebugMessage("  SkipUnchanged: {0}", SkipUnchanged);

			if (!Directory.Exists(OutputDirectory)) {
				Directory.CreateDirectory(OutputDirectory);
			}

			var outputFiles = new List<string>();
			var bundledConfigFiles = new List<string>();
			foreach (var assembly in Assemblies) {
				var outputFile = Path.Combine(OutputDirectory, Path.GetFileName(assembly.ItemSpec) + ".bundle.c");
				string configFile;
				GenerateBundledAssembly(assembly.ItemSpec, outputFile, out configFile);
				outputFiles.Add(outputFile);
				if (configFile != null) {
					bundledConfigFiles.Add(configFile);
				}
			}
			GeneratedFiles = outputFiles.Select(f => new TaskItem(f)).ToArray();
			BundledConfigFiles = bundledConfigFiles.Select(f => new TaskItem(f)).ToArray();
			Log.LogDebugTaskItems("  [Output] GeneratedFiles:", GeneratedFiles);
			Log.LogDebugTaskItems("  [Output] BundledConfigFiles:", BundledConfigFiles);

			return true;
		}

		private static void WriteFileAsCArray(Stream ins, StreamWriter outs)
		{
			int n;
			var buffer = new byte[80 / 5];
			while ((n = ins.Read(buffer, 0, buffer.Length)) != 0) {
				outs.Write("\t");
				for (var i = 0; i < n; i++) {
					outs.Write("0x");
					outs.Write(buffer[i].ToString("X2"));
					outs.Write(",");
				}
				outs.WriteLine();
			}
		}

		public void GenerateBundledAssembly(string assemblyFile, string outputFile, out string configFile)
		{
			var assemblyName = Symbols.GetBundledAssemblyName(assemblyFile, Log);
			var assemblyConfigFileCopy = Path.Combine(Path.GetDirectoryName(outputFile), assemblyName) + ".config";
			var configName = (assemblyName + ".config").ToLower();
			try {
				configFile = ConfigFiles.First(t => Path.GetFileName(t.ItemSpec ?? "").ToLower() == configName).ItemSpec;
			} catch {
				configFile = null;
			}

			if (SkipUnchanged && File.Exists(outputFile) && File.Exists(assemblyFile) 
						&& File.GetLastWriteTime(outputFile) >= File.GetLastWriteTime(assemblyFile)) {
				if (configFile == null && !File.Exists(assemblyConfigFileCopy)
					|| configFile != null && File.Exists(assemblyConfigFileCopy) && File.GetLastWriteTime(configFile) >= File.GetLastWriteTime(assemblyConfigFileCopy)) {
					Log.LogDebugMessage("  Not regenerating bundle file for unchanged assembly: {0}", assemblyFile);
					return;
				}
			}

			Log.LogDebugMessage($"  Generating output '{outputFile}' from assembly '{assemblyFile}'");
			var bundledAssemblyGetter = Symbols.GetBundledAssemblyGetter(assemblyName);
			var bundledAssemblyConfigGetter = Symbols.GetBundledAssemblyConfigGetter(assemblyName);
			var bundledAssemblyCleanup = Symbols.GetBundledAssemblyCleanup(assemblyName);
			using (var ins = File.OpenRead(assemblyFile)) {
				using (var outs = new StreamWriter(File.Create(outputFile))) {
					outs.WriteLine("static const unsigned char bundle_data [] = {");
					WriteFileAsCArray(ins, outs);
					outs.WriteLine("};");
					outs.WriteLine("typedef struct { const char* name; const unsigned char* data; const unsigned int size; } MonoBundledAssembly;");
					outs.WriteLine($"static const MonoBundledAssembly bundle = {{\"{assemblyName}\", bundle_data, sizeof (bundle_data)}};");
					outs.WriteLine($"const MonoBundledAssembly *{bundledAssemblyGetter} (void) {{ return &bundle; }}");

					outs.WriteLine("typedef struct { const char* name; const char* data; } MonoBundledAssemblyConfig;");
					if (configFile != null) {
						try {
							using (var cfgs = File.OpenRead(configFile)) {
								Log.LogDebugMessage($"    Found assembly config file '{configFile}' for assembly '{assemblyFile}'");
								outs.WriteLine("static const char config_data [] = {");
								WriteFileAsCArray(cfgs, outs);
								outs.WriteLine("0};");
								outs.WriteLine($"static const MonoBundledAssemblyConfig config = {{\"{assemblyName}\", config_data}};");
							}
						} catch {
							// Return NULL if the assembly has no config file.
							configFile = null;
						}
					}
					if (configFile == null) {
						Log.LogDebugMessage($"    No assembly config file found for assembly '{assemblyFile}'");
						outs.WriteLine($"static const MonoBundledAssemblyConfig config = {{\"{assemblyName}\", 0L}};");
					}
					if (File.Exists(assemblyConfigFileCopy)) {
						File.Delete(assemblyConfigFileCopy);
					}
					if (configFile != null) {
						File.Copy(configFile, assemblyConfigFileCopy);
					}
					outs.WriteLine($"const MonoBundledAssemblyConfig *{bundledAssemblyConfigGetter} (void) {{ return &config; }}");

					// Cleanup function does nothing for now. Will be needed for compressed bundled.
					outs.WriteLine($"void {bundledAssemblyCleanup} (void) {{ return; }}");
				}
			}
		}
	}

}
