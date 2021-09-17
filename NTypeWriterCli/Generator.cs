using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.Workspaces;
using Microsoft.CodeAnalysis;
using NTypewriter;
using NTypewriter.CodeModel.Functions;
using NTypewriter.CodeModel.Roslyn;
using NTypewriter.Editor.Config;

namespace NTypeWriterCli
{
    class Generator
    {
        public bool Verbose { get; set; }

        private AnalyzerManager _analyzerManager;
        private AdhocWorkspace _workspace;
        private StringWriter _log;

        public void LoadSolution(string solutionPath)
        {
            _log = new StringWriter();
            var options = new AnalyzerManagerOptions
            {
                LogWriter = _log
            };
            if (Verbose) Console.Error.WriteLine($"[-] Loading solution from '{solutionPath}'");
            _analyzerManager = new AnalyzerManager(solutionPath, options);
            if (Verbose) Console.Error.WriteLine($"[-] Found {_analyzerManager.Projects.Count} projects");
            _workspace = _analyzerManager.GetWorkspace();
        }

        public void LoadProject(string projectPath)
        {
            _log = new StringWriter();
            var options = new AnalyzerManagerOptions
            {
                LogWriter = _log
            };
            if (Verbose) Console.Error.WriteLine($"[-] Loading project from '{projectPath}'");
            _analyzerManager = new AnalyzerManager(options);
            _workspace = _analyzerManager.GetProject(projectPath).GetWorkspace();
        }

        public async Task Generate()
        {
            var projectStatus = new Dictionary<string, bool>();
            var projectTemplates = new Dictionary<string, List<string>>();

            if (Verbose) await Console.Error.WriteLineAsync($"[-] Discovering projects");
            foreach (var project in _workspace.CurrentSolution.Projects)
            {
                if (!project.SupportsCompilation)
                {
                    await Console.Error.WriteLineAsync($"[!] Compiling '{project.Name}' is not supported");
                    continue;
                }

                if (project.FilePath == null)
                {
                    await Console.Error.WriteLineAsync(
                        $"[!] Project '{project.Name}' lacks a project file, which is not supported");
                    continue;
                }

                if (Verbose) await Console.Error.WriteLineAsync($"[-] Looking for NTypeWriter templates in project '{project.Name}'");
                /*var templates = project.Documents.Select(x => x.FilePath)
                    .Where(x => x?.ToLowerInvariant()?.EndsWith(".nt") == true)
                    .ToList();*/
                var templates = Directory.EnumerateFiles(Path.GetDirectoryName(project.FilePath), "*.nt",
                    SearchOption.AllDirectories).ToList();
                if (Verbose) await Console.Error.WriteLineAsync($"[-] Found {templates.Count} templates in project");
                if (templates.Count != 0)
                {
                    projectTemplates.Add(project.FilePath, templates);
                }

                projectStatus.Add(project.FilePath, true);
            }

            if (Verbose) await Console.Error.WriteLineAsync("[-] Generating based on templates");
            foreach (var project in _workspace.CurrentSolution.Projects.Where(x => x.FilePath != null && projectTemplates.ContainsKey(x.FilePath)))
            {
                var configClass = await GetEditorConfig(project);
                var twConfiguration = new Configuration();
                if (configClass == null)
                {
                    await Console.Error.WriteLineAsync($"[-] Found no config for '{project.Name}', using defaults.");
                    configClass = new EditorConfig();
                }

                foreach (var typeThatContainCustomFunction in configClass.TypesThatContainCustomFunctions)
                {
                    twConfiguration.AddCustomFunctions(typeThatContainCustomFunction);
                }

                var projects = _workspace.CurrentSolution.Projects;
                if (configClass.ProjectsToBeSearched.Count() != 0)
                {
                    projects = projects.Where(x => configClass.ProjectsToBeSearched.Contains(x.Name)).ToList();
                }

                if (Verbose) await Console.Error.WriteLineAsync($"[-] Generating code model for '{projects.Count()}' projects");

                var codeModelConfiguration = new CodeModelConfiguration()
                {
                    OmitSymbolsFromReferencedAssemblies = !configClass.SearchInReferencedProjectsAndAssemblies
                };
                foreach (var namespaceStr in configClass.NamespacesToBeSearched)
                {
                    codeModelConfiguration.FilterByNamespace(namespaceStr);
                }

                var compilations = await Task.WhenAll(projects.Select(async x =>
                {
                    var compilation = await x.GetCompilationAsync();
                    if (compilation == null || !compilation.SyntaxTrees.Any())
                    {
                        throw new Exception($"Compiling '{x.FilePath} failed");
                    }
                    return compilation;
                }));
                
                var codeModel = new CombinedCodeModel(compilations.Select(x => new CodeModel(x, codeModelConfiguration)));

                foreach (var templatePath in projectTemplates[project.FilePath])
                {
                    if (Verbose) await Console.Error.WriteLineAsync($"[-] Processing template '{templatePath}'");
                    var template = await File.ReadAllTextAsync(templatePath);
                    var result = await NTypeWriter.Render(template, codeModel, twConfiguration);

                    if (!result.HasErrors)
                    {
                        foreach (var renderedItem in result.Items)
                        {
                            var path = Path.Combine(Path.GetDirectoryName(templatePath), renderedItem.Name);
                            if (Verbose) await Console.Error.WriteLineAsync($"[-] Saving captured output to '{path}'");

                            var targetDirectory = Path.GetDirectoryName(path);
                            if (!Directory.Exists(targetDirectory))
                            {
                                if (Verbose) await Console.Error.WriteLineAsync($"[-] Creating directory path '{targetDirectory}'");
                                Directory.CreateDirectory(targetDirectory);
                            }

                            File.WriteAllText(path, renderedItem.Content);
                        }
                    }
                    else
                    {
                        await Console.Error.WriteLineAsync($"[X] NTypeWriter returned the following errors:");
                        foreach (var msg in result.Messages)
                        {
                            await Console.Error.WriteLineAsync($"      {msg}");
                        }
                        Environment.Exit(1);
                    }
                }
            }
        }

        private async Task<IEditorConfig> GetEditorConfig(Project project)
        {
            if (Verbose) await Console.Error.WriteLineAsync($"[-] Compiling project '{project.Name}'");
            
            var compilation = await project.GetCompilationAsync();
            if (compilation == null || !compilation.SyntaxTrees.Any())
            {
                if (Verbose) await Console.Error.WriteLineAsync($"[!] Failed to compile project '{project.Name}'");
                return null;
            }

            if (Verbose) await Console.Error.WriteLineAsync($"[-] Creating config code model for project '{project.Name}'");
            var codeModelConfiguration = new CodeModelConfiguration() { OmitSymbolsFromReferencedAssemblies = true };
            var codeModel = new CodeModel(compilation, codeModelConfiguration);

            if (Verbose) await Console.Error.WriteLineAsync($"[-] Looking for NTypeWriter global config in project '{project.Name}'");
            var candidates = codeModel.Classes.Where(x => x.HasAttribute("NTEditorFile")).ToList();
            if (candidates.Count > 1)
            {
                await Console.Error.WriteLineAsync($"[!] Found more than one potential config in '{project.Name}'");
            }
            var files = candidates.SelectMany(x => Directory.EnumerateFiles(Path.GetDirectoryName(project.FilePath), "*.cs", SearchOption.AllDirectories)
                    .Where(y => y?.ToLowerInvariant().EndsWith($"{x.Name.ToLowerInvariant()}.cs") == true)).ToList();
            if (files.Count > 1)
            {
                await Console.Error.WriteLineAsync($"[!] Found more than one potential file for our config in '{project.Name}'");
            }
            if (files.Count == 0)
            {
                await Console.Error.WriteLineAsync($"[X] Found no potential files for our config in '{project.Name}', make sure the file has the same name as the class.");
                return null;
            }

            var projPath = project.FilePath;
            if (!Path.IsPathFullyQualified(projPath))
            {
                projPath = Path.GetFullPath(projPath);
            }
            if (!File.Exists(projPath))
            {
                await Console.Error.WriteLineAsync($"[!] Can't find the project file for '{project.Name}' (looked at '{projPath}')");
                return null;
            }

            projPath = Path.GetDirectoryName(projPath);

            var filePaths = files.Select(x =>
            {
                if (!Path.IsPathFullyQualified(x))
                {
                    return Path.GetFullPath(x, projPath);
                }

                return x;
            });
            var tempProj = await CreateTemporaryProject(filePaths);

            if (Verbose) await Console.Error.WriteLineAsync($"[-] Building .NET Standard assembly with config (located at '{tempProj}')");
            var confAnalyzerManager = new AnalyzerManager();
            var confResults = confAnalyzerManager.GetProject(tempProj)
                .Build(new EnvironmentOptions()
                {
                    DesignTime = false,
                    Preference = EnvironmentPreference.Core,
                    Restore = true
                });
            if (!confResults.OverallSuccess)
            {
                await Console.Error.WriteLineAsync($"[!] Failed to compile .NET Standard assembly with config (located at '{tempProj}')");
                return null;
            }

            var configAssemblyPath = Path.Join(Path.GetDirectoryName(tempProj), "bin", "Debug", "netstandard2.0", "Config.dll");
            if (!File.Exists(configAssemblyPath))
            {
                await Console.Error.WriteLineAsync($"[!] Failed to find compiled .NET Standard assembly with config (looked at '{configAssemblyPath}')");
                return null;
            }

            if (Verbose) await Console.Error.WriteLineAsync($"[-] Loading .NET Standard assembly with config using reflection");
            var configAssembly = Assembly.LoadFile(configAssemblyPath);
            var configType = configAssembly.GetTypes().Single(x => x.GetInterfaces().Contains(typeof(IEditorConfig)));
            return (IEditorConfig)Activator.CreateInstance(configType);
        }

        private async Task<string> CreateTemporaryProject(IEnumerable<string> files)
        {
            if (Verbose)
            {
                await Console.Error.WriteLineAsync("[!] Creating temporary project with:");
                foreach (var file in files)
                {
                    await Console.Error.WriteLineAsync($"      {file}");
                }
            }

            var folderPath = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(folderPath);

            var csprojPath = Path.Join(folderPath, "Config.csproj");

            var assembly = Assembly.GetExecutingAssembly();
            await using var stubProjectStream = assembly.GetManifestResourceStream("NTypeWriterCli.NetStandardStub.cspro_");
            if (stubProjectStream == null)
                throw new Exception("Failed to find NetStandardStub in current DLL???");
            await using var projectFile = File.OpenWrite(csprojPath);
            await stubProjectStream.CopyToAsync(projectFile);

            foreach (var file in files)
            {
                File.Copy(file, Path.Join(folderPath, Path.GetFileName(file)));
            }

            return csprojPath;
        }
    }
}
