// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
// 
// This file is based on the work from https://github.com/praeclarum/Ooui
// 
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace Uno.Wasm.Bootstrap
{
	public partial class ShellTask_v0 : Microsoft.Build.Utilities.Task
	{
		private const string DefaultSdkUrl = "https://jenkins.mono-project.com/job/test-mono-mainline-wasm/label=ubuntu-1804-amd64/598/Azure/processDownloadRequest/598/ubuntu-1804-amd64/sdks/wasm/mono-wasm-f07691d5125.zip";

		private string _distPath;
		private string _managedPath;
		private string _bclPath;
		private List<string> _linkedAsmPaths;
		private List<string> _referencedAssemblies;
		private Dictionary<string, string> _bclAssemblies;
		private string _sdkPath;
		private List<string> _dependencies = new List<string>();
		private string[] _additionalStyles;

		[Microsoft.Build.Framework.Required]
		public string Assembly { get; set; }

		[Microsoft.Build.Framework.Required]
		public string OutputPath { get; set; }

		public Microsoft.Build.Framework.ITaskItem[] ReferencePath { get; set; }

		[Microsoft.Build.Framework.Required]
		public string TargetFrameworkIdentifier { get; set; }

		[Microsoft.Build.Framework.Required]
		public string IndexHtmlPath { get; set; }

		public string MonoWasmSDKUri { get; set; }

		public string AssembliesFileExtension { get; set; } = "clr";

		public Microsoft.Build.Framework.ITaskItem[] Assets { get; set; }

		public Microsoft.Build.Framework.ITaskItem[] LinkerDescriptors { get; set; }

		[Microsoft.Build.Framework.Required]
		public string RuntimeConfiguration { get; set; }

		[Microsoft.Build.Framework.Required]
		public bool RuntimeDebuggerEnabled { get; set; }

		public string PWAManifestFile { get; set; }

		public override bool Execute()
		{
			try
			{
				if(TargetFrameworkIdentifier != ".NETStandard")
				{
					Log.LogWarning($"The package Uno.Wasm.Bootstrap is not supported for the current project ({Assembly}), skipping dist generation.");
					return true;
				}

				InstallSdk();
				GetBcl();
				CreateDist();
				CopyContent();
				CopyRuntime();
				LinkAssemblies();
				HashManagedPath();
				ExtractAdditionalJS();
				ExtractAdditionalCSS();
				GenerateHtml();
				return true;
			}
			catch (Exception ex)
			{
				Log.LogErrorFromException(ex, false, true, null);
				return false;
			}
		}

		private void HashManagedPath()
		{
			var hashFunction = SHA1.Create();

			IEnumerable<byte> ComputeHash(string file)
			{
				using (var s = File.OpenRead(file))
				{
					return hashFunction.ComputeHash(s);
				}
			}

			var allBytes = Directory.GetFiles(_managedPath)
				.Select(ComputeHash)
				.SelectMany(h => h)
				.ToArray();

			var hash = string.Join("", hashFunction.ComputeHash(allBytes).Select(b => b.ToString("x2")));

			var oldManagedPath = _managedPath;
			_managedPath = _managedPath + "-" + hash;
			Directory.Move(oldManagedPath, _managedPath);
		}

		private void InstallSdk()
		{
			var sdkUri = string.IsNullOrWhiteSpace(MonoWasmSDKUri) ? DefaultSdkUrl : MonoWasmSDKUri;

			try
			{
				var sdkName = Path.GetFileNameWithoutExtension(new Uri(sdkUri).AbsolutePath.Replace('/', Path.DirectorySeparatorChar));
				Log.LogMessage("SDK: " + sdkName);
				_sdkPath = Path.Combine(Path.GetTempPath(), sdkName);
				Log.LogMessage("SDK Path: " + _sdkPath);

				if (Directory.Exists(_sdkPath))
				{
					return;
				}

				var client = new WebClient();
				var zipPath = _sdkPath + ".zip";
				Log.LogMessage($"Using mono-wasm SDK {sdkUri}");
				Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High, $"Downloading {sdkName} to {zipPath}");
				client.DownloadFile(sdkUri, zipPath);

				ZipFile.ExtractToDirectory(zipPath, _sdkPath);
				Log.LogMessage($"Extracted {sdkName} to {_sdkPath}");
			}
			catch(Exception e)
			{
				throw new InvalidOperationException($"Failed to download the mono-wasm SDK at {sdkUri}");
			}
		}

		private void GetBcl()
		{
			_bclPath = Path.Combine(_sdkPath, "bcl");
			var reals = Directory.GetFiles(_bclPath, "*.dll");
			var facades = Directory.GetFiles(Path.Combine(_bclPath, "Facades"), "*.dll");
			var allFiles = reals.Concat(facades);
			_bclAssemblies = allFiles.ToDictionary(x => Path.GetFileName(x));
		}

		private void CreateDist()
		{
			var outputPath = Path.GetFullPath(OutputPath);
			_distPath = Path.Combine(outputPath, "dist");
			_managedPath = Path.Combine(_distPath, "managed");
			Directory.CreateDirectory(_managedPath);
		}

		private void CopyRuntime()
		{
			var runtimePath = Path.Combine(_sdkPath, RuntimeConfiguration.ToLower());

			foreach (var sourceFile in Directory.EnumerateFiles(runtimePath))
			{
				var dest = Path.Combine(_distPath, Path.GetFileName(sourceFile));
				Log.LogMessage($"Runtime {sourceFile} -> {dest}");
				File.Copy(sourceFile, dest, true);
			}

			File.Copy(Path.Combine(_sdkPath, "server.py"), Path.Combine(_distPath, "server.py"), true);
		}

		private void CopyContent()
		{
			if (Assets != null)
			{
				var runtimePath = Path.Combine(_sdkPath, RuntimeConfiguration.ToLower());

				foreach (var sourceFile in Assets)
				{
					(string fullPath, string relativePath) GetFilePaths()
					{
						if (sourceFile.GetMetadata("Link") is string link && !string.IsNullOrEmpty(link))
						{
							// This case is mainly for shared projects
							return (sourceFile.ItemSpec, link);
						}
						else if (sourceFile.GetMetadata("FullPath") is string fullPath && File.Exists(fullPath))
						{
							// This is fore files added explicitly through other targets (e.g. Microsoft.TypeScript.MSBuild)
							return (fullPath, sourceFile.ToString());
						}
						else
						{
							// This is for project-local defined content
							var baseSourceFile = sourceFile.GetMetadata("DefiningProjectDirectory");

							return (Path.Combine(baseSourceFile, sourceFile.ItemSpec), sourceFile.ToString());
						}
					}

					(var fullSourcePath, var relativePath) = GetFilePaths();

					Directory.CreateDirectory(Path.Combine(_distPath, Path.GetDirectoryName(relativePath)));

					var dest = Path.Combine(_distPath, relativePath);
					Log.LogMessage($"ContentFile {fullSourcePath} -> {dest}");
					File.Copy(fullSourcePath, dest, true);
				}
			}
		}

		private void ExtractAdditionalJS()
		{
			var q = EnumerateResources("js", "WasmDist")
				.Concat(EnumerateResources("js", "WasmScripts"));

			foreach(var (name, source, resource) in q)
			{ 
				if (source.Name.Name != GetType().Assembly.GetName().Name)
				{
					_dependencies.Add(name);
				}

				CopyResourceToOutput(name, resource);

				Log.LogMessage($"Additional JS {name}");
			}
		}

		private void ExtractAdditionalCSS()
		{
			var q = EnumerateResources("css", "WasmCSS");

			foreach (var (name, source, resource) in q)
			{
				using (var srcs = resource.GetResourceStream())
				{
					CopyResourceToOutput(name, resource);

					Log.LogMessage($"Additional CSS {name}");
				}
			}

			_additionalStyles = q
				.Select(res => res.name)
				.ToArray();
		}

		private void CopyResourceToOutput(string name, EmbeddedResource resource)
		{
			var dest = Path.Combine(_distPath, name);

			using (var srcs = resource.GetResourceStream())
			{
				using (var dests = new FileStream(dest, FileMode.Create, FileAccess.Write))
				{
					srcs.CopyTo(dests);
				}
			}
		}

		private IEnumerable<(string name, AssemblyDefinition source, EmbeddedResource resource)> EnumerateResources(string extension, string folder)
		{
			var fullExtension = "." + extension;
			var fullFolder = "." + folder + ".";

			return from asmPath in _referencedAssemblies.Concat(new[] { Assembly, this.GetType().Assembly.Location })
				   let asm = AssemblyDefinition.ReadAssembly(asmPath)
				   from res in asm.MainModule.Resources.OfType<EmbeddedResource>()
				   where res.Name.EndsWith(fullExtension)
				   where res.Name.Contains(fullFolder)
				   select (
					name: res.Name.Substring(res.Name.IndexOf(fullFolder) + fullFolder.Length),
					source: asm,
					resource: res
					);
		}

		private MethodDefinition DiscoverEntryPoint()
		{
			var asm = AssemblyDefinition.ReadAssembly(Assembly);

			if (asm?.EntryPoint is MethodDefinition def)
			{
				return def;
			}

			throw new Exception($"{Path.GetFileName(Assembly)} is missing an entry point. Add <OutputType>Exe</OutputType> in the project file and a static main.");
		}

		private void GenerateHtml()
		{
			var htmlPath = Path.Combine(_distPath, "index.html");

			var entryPoint = DiscoverEntryPoint();

			using (var w = new StreamWriter(htmlPath, false, new UTF8Encoding(false)))
			{
				using (var reader = new StreamReader(IndexHtmlPath))
				{
					var html = reader.ReadToEnd();

					var assemblies = string.Join(", ", _linkedAsmPaths.Select(x => $"\"{Path.GetFileName(x)}\""));
					var dependencies = string.Join(", ", _dependencies.Select(x => $"\"{Path.GetFileNameWithoutExtension(x)}\""));

					html = html.Replace("$(ASSEMBLIES_LIST)", assemblies);
					html = html.Replace("$(DEPENDENCIES_LIST)", dependencies);
					html = html.Replace("$(MAIN_ASSEMBLY_NAME)", entryPoint.DeclaringType.Module.Assembly.Name.Name);
					html = html.Replace("$(MAIN_NAMESPACE)", entryPoint.DeclaringType.Namespace);
					html = html.Replace("$(MAIN_TYPENAME)", entryPoint.DeclaringType.Name);
					html = html.Replace("$(MAIN_METHOD)", entryPoint.Name);
					html = html.Replace("$(ENABLE_RUNTIMEDEBUG)", RuntimeDebuggerEnabled.ToString().ToLower());
					html = html.Replace("$(REMOTE_MANAGED_PATH)", Path.GetFileName(_managedPath));
					html = html.Replace("$(ASSEMBLY_FILE_EXTENSION)", AssembliesFileExtension);

					var styles = string.Join("\r\n", _additionalStyles.Select(s => $"<link rel=\"stylesheet\" type=\"text/css\" href=\"{s}\" />"));
					html = html.Replace("$(ADDITIONAL_CSS)", styles);

					var extraBuilder = new StringBuilder();
					if (!string.IsNullOrWhiteSpace(PWAManifestFile))
					{
						extraBuilder.AppendLine($"<link rel=\"manifest\" href=\"{PWAManifestFile}\" />");
					}

					html = html.Replace("$(ADDITIONAL_HEAD)", extraBuilder.ToString());

					w.Write(html);


					Log.LogMessage($"HTML {htmlPath}");
				}

			}
		}
	}
}
