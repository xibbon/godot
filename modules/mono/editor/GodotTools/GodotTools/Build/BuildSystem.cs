using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Godot;
using GodotTools.BuildLogger;
using GodotTools.Internals;
using GodotTools.Utils;
using Directory = GodotTools.Utils.Directory;

namespace GodotTools.Build
{
    public static class BuildSystem
    {
        // The downloadable Xogot component carries the exact Godot NuGet packages
        // that match the embedded engine. Development and prerelease engine builds
        // are not necessarily published on nuget.org, so the project must use this
        // source before MSBuild resolves the Godot.NET.Sdk declaration.
        private const string XogotComponentNuGetSourceName = "xogot-dotnet-component";
        private static readonly TimeSpan BuildCancellationGracePeriod = TimeSpan.FromSeconds(5);

        // `dotnet build` is a console process. On the platforms Xogot embeds on, SIGINT lets
        // dotnet/MSBuild flush its logs and clean up first; the delayed tree kill below is the
        // hard stop for a compiler that ignores the request.
        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        private static void EnsureXogotComponentNuGetSource(BuildInfo buildInfo)
        {
            if (!OperatingSystem.IsMacOS() ||
                System.Environment.GetEnvironmentVariable("XOGOT_OPTIONAL_DOTNET_COMPONENT") != "1")
            {
                return;
            }

            string nupkgsDir = Path.Combine(Internals.GodotSharpDirs.DataEditorToolsDir, "nupkgs");
            if (!System.IO.Directory.Exists(nupkgsDir))
                return;

            string? projectDir = Path.GetDirectoryName(buildInfo.Project);
            if (string.IsNullOrEmpty(projectDir))
                return;

            // A project-local NuGet.Config also makes `dotnet build` work from an
            // external terminal or IDE. Preserve all user sources and update only
            // the source that Xogot owns, so component updates move it safely.
            string configPath = Path.Combine(projectDir, "NuGet.Config");
            if (!System.IO.File.Exists(configPath))
            {
                string lowercaseConfigPath = Path.Combine(projectDir, "nuget.config");
                if (System.IO.File.Exists(lowercaseConfigPath))
                    configPath = lowercaseConfigPath;
            }

            XDocument document = System.IO.File.Exists(configPath)
                ? XDocument.Load(configPath, LoadOptions.PreserveWhitespace)
                : new XDocument(new XElement("configuration"));

            XElement configuration = document.Root
                ?? throw new InvalidDataException($"NuGet configuration is empty: {configPath}");
            if (!string.Equals(configuration.Name.LocalName, "configuration", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid NuGet configuration root in {configPath}.");

            XElement? packageSources = configuration.Element("packageSources");
            if (packageSources == null)
            {
                packageSources = new XElement("packageSources");
                configuration.Add(packageSources);
            }

            XElement? source = packageSources.Elements("add")
                .FirstOrDefault(element => string.Equals(
                    (string?)element.Attribute("key"),
                    XogotComponentNuGetSourceName,
                    StringComparison.Ordinal));
            if (source == null)
            {
                source = new XElement("add", new XAttribute("key", XogotComponentNuGetSourceName));
                packageSources.Add(source);
            }

            if (string.Equals((string?)source.Attribute("value"), nupkgsDir, StringComparison.Ordinal))
                return;

            source.SetAttributeValue("value", nupkgsDir);
            document.Save(configPath);
        }

        private static Process LaunchBuild(BuildInfo buildInfo, Action<string?>? stdOutHandler,
            Action<string?>? stdErrHandler)
        {
            string? dotnetPath = DotNetFinder.FindDotNetExe();

            if (dotnetPath == null)
                throw new FileNotFoundException("Cannot find the dotnet executable.");

            var editorSettings = EditorInterface.Singleton.GetEditorSettings();

            EnsureXogotComponentNuGetSource(buildInfo);

            var startInfo = new ProcessStartInfo(dotnetPath);

            BuildArguments(buildInfo, startInfo.ArgumentList, editorSettings);

            string launchMessage = startInfo.GetCommandLineDisplay(new StringBuilder("Running: ")).ToString();
            stdOutHandler?.Invoke(launchMessage);
            if (Godot.OS.IsStdOutVerbose())
                Console.WriteLine(launchMessage);

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"]
                = ((string)EditorInterface.Singleton.GetEditorLanguage()).Replace('_', '-');

            if (OperatingSystem.IsWindows())
            {
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }

            // Needed when running from Developer Command Prompt for VS
            RemovePlatformVariable(startInfo.EnvironmentVariables);

            var process = new Process { StartInfo = startInfo };

            if (stdOutHandler != null)
                process.OutputDataReceived += (_, e) => stdOutHandler.Invoke(e.Data);
            if (stdErrHandler != null)
                process.ErrorDataReceived += (_, e) => stdErrHandler.Invoke(e.Data);

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }

        public static int Build(BuildInfo buildInfo, Action<string?>? stdOutHandler, Action<string?>? stdErrHandler)
        {
            using (var process = LaunchBuild(buildInfo, stdOutHandler, stdErrHandler))
            {
                process.WaitForExit();

                return process.ExitCode;
            }
        }

        public static async Task<int> BuildAsync(BuildInfo buildInfo, Action<string?>? stdOutHandler,
            Action<string?>? stdErrHandler, CancellationToken cancellationToken = default)
        {
            using (var process = LaunchBuild(buildInfo, stdOutHandler, stdErrHandler))
            {
                Task waitForExit = process.WaitForExitAsync();
                if (cancellationToken.CanBeCanceled)
                {
                    Task cancellationRequested = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    if (await Task.WhenAny(waitForExit, cancellationRequested) != waitForExit && !process.HasExited)
                    {
                        TryInterrupt(process);
                        if (await Task.WhenAny(waitForExit, Task.Delay(BuildCancellationGracePeriod)) != waitForExit &&
                            !process.HasExited)
                        {
                            try
                            {
                                process.Kill(entireProcessTree: true);
                            }
                            catch (InvalidOperationException)
                            {
                                // The process exited between HasExited and Kill.
                            }
                        }
                    }
                }

                await waitForExit;

                return process.ExitCode;
            }
        }

        private static void TryInterrupt(Process process)
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                return;

            try
            {
                _ = kill(process.Id, 2); // SIGINT
            }
            catch (InvalidOperationException)
            {
                // The process has already exited.
            }
        }

        private static Process LaunchPublish(BuildInfo buildInfo, Action<string?>? stdOutHandler,
            Action<string?>? stdErrHandler)
        {
            string? dotnetPath = DotNetFinder.FindDotNetExe();

            if (dotnetPath == null)
                throw new FileNotFoundException("Cannot find the dotnet executable.");

            var editorSettings = EditorInterface.Singleton.GetEditorSettings();

            var startInfo = new ProcessStartInfo(dotnetPath);

            BuildPublishArguments(buildInfo, startInfo.ArgumentList, editorSettings);

            string launchMessage = startInfo.GetCommandLineDisplay(new StringBuilder("Running: ")).ToString();
            stdOutHandler?.Invoke(launchMessage);
            if (Godot.OS.IsStdOutVerbose())
                Console.WriteLine(launchMessage);

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"]
                = ((string)EditorInterface.Singleton.GetEditorLanguage()).Replace('_', '-');

            if (OperatingSystem.IsWindows())
            {
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }

            // Needed when running from Developer Command Prompt for VS
            RemovePlatformVariable(startInfo.EnvironmentVariables);

            var process = new Process { StartInfo = startInfo };

            if (stdOutHandler != null)
                process.OutputDataReceived += (_, e) => stdOutHandler.Invoke(e.Data);
            if (stdErrHandler != null)
                process.ErrorDataReceived += (_, e) => stdErrHandler.Invoke(e.Data);

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }

        public static int Publish(BuildInfo buildInfo, Action<string?>? stdOutHandler, Action<string?>? stdErrHandler)
        {
            using (var process = LaunchPublish(buildInfo, stdOutHandler, stdErrHandler))
            {
                process.WaitForExit();

                return process.ExitCode;
            }
        }

        private static void BuildArguments(BuildInfo buildInfo, Collection<string> arguments,
            EditorSettings editorSettings)
        {
            // `dotnet clean` / `dotnet build` commands
            arguments.Add(buildInfo.OnlyClean ? "clean" : "build");

            // C# Project
            arguments.Add(buildInfo.Project);

            // `dotnet clean` doesn't recognize these options
            if (!buildInfo.OnlyClean)
            {
                // Restore
                // `dotnet build` restores by default, unless requested not to
                if (!buildInfo.Restore)
                    arguments.Add("--no-restore");

                // Incremental or rebuild
                if (buildInfo.Rebuild)
                    arguments.Add("--no-incremental");
            }

            // Configuration
            arguments.Add("-c");
            arguments.Add(buildInfo.Configuration);

            // Verbosity
            AddVerbosityArguments(buildInfo, arguments, editorSettings);

            // Logger
            AddLoggerArgument(buildInfo, arguments);

            // Binary log
            AddBinaryLogArgument(buildInfo, arguments, editorSettings);

            // Custom properties
            foreach (var customProperty in buildInfo.CustomProperties)
            {
                arguments.Add("-p:" + (string)customProperty);
            }
        }

        private static void BuildPublishArguments(BuildInfo buildInfo, Collection<string> arguments,
            EditorSettings editorSettings)
        {
            arguments.Add("publish"); // `dotnet publish` command

            // C# Project
            arguments.Add(buildInfo.Project);

            // Restore
            // `dotnet publish` restores by default, unless requested not to
            if (!buildInfo.Restore)
                arguments.Add("--no-restore");

            // Incremental or rebuild
            // TODO: Not supported in `dotnet publish` (https://github.com/dotnet/sdk/issues/11099)
            // if (buildInfo.Rebuild)
            //     arguments.Add("--no-incremental");

            // Configuration
            arguments.Add("-c");
            arguments.Add(buildInfo.Configuration);

            // Runtime Identifier
            arguments.Add("-r");
            arguments.Add(buildInfo.RuntimeIdentifier!);

            // Self-published
            arguments.Add("--self-contained");
            arguments.Add("true");

            // Verbosity
            AddVerbosityArguments(buildInfo, arguments, editorSettings);

            // Logger
            AddLoggerArgument(buildInfo, arguments);

            // Binary log
            AddBinaryLogArgument(buildInfo, arguments, editorSettings);

            // Custom properties
            foreach (var customProperty in buildInfo.CustomProperties)
            {
                arguments.Add("-p:" + (string)customProperty);
            }

            // Publish output directory
            if (buildInfo.PublishOutputDir != null)
            {
                arguments.Add("-o");
                arguments.Add(buildInfo.PublishOutputDir);
            }
        }

        private static void AddVerbosityArguments(BuildInfo buildInfo, Collection<string> arguments,
            EditorSettings editorSettings)
        {
            var verbosityLevel =
                editorSettings.GetSetting(GodotSharpEditor.Settings.VerbosityLevel).As<VerbosityLevelId>();
            arguments.Add("-v");
            arguments.Add(verbosityLevel switch
            {
                VerbosityLevelId.Quiet => "quiet",
                VerbosityLevelId.Minimal => "minimal",
                VerbosityLevelId.Detailed => "detailed",
                VerbosityLevelId.Diagnostic => "diagnostic",
                _ => "normal",
            });

            if ((bool)editorSettings.GetSetting(GodotSharpEditor.Settings.NoConsoleLogging))
                arguments.Add("-noconlog");
        }

        private static void AddLoggerArgument(BuildInfo buildInfo, Collection<string> arguments)
        {
            string buildLoggerPath = Path.Combine(Internals.GodotSharpDirs.DataEditorToolsDir,
                "GodotTools.BuildLogger.dll");

            arguments.Add(
                $"-l:{typeof(GodotBuildLogger).FullName},{buildLoggerPath};{buildInfo.LogsDirPath}");
        }

        private static void AddBinaryLogArgument(BuildInfo buildInfo, Collection<string> arguments,
            EditorSettings editorSettings)
        {
            if (!(bool)editorSettings.GetSetting(GodotSharpEditor.Settings.CreateBinaryLog))
                return;

            arguments.Add($"-bl:{Path.Combine(buildInfo.LogsDirPath, "msbuild.binlog")}");
            arguments.Add("-ds:False"); // Honestly never understood why -bl also switches -ds on.
        }

        private static void RemovePlatformVariable(StringDictionary environmentVariables)
        {
            // EnvironmentVariables is case sensitive? Seriously?

            var platformEnvironmentVariables = new List<string>();

            foreach (string env in environmentVariables.Keys)
            {
                if (env.ToUpperInvariant() == "PLATFORM")
                    platformEnvironmentVariables.Add(env);
            }

            foreach (string env in platformEnvironmentVariables)
                environmentVariables.Remove(env);
        }

        private static Process DoGenerateXCFramework(List<string> outputPaths, string xcFrameworkPath,
            Action<string?>? stdOutHandler, Action<string?>? stdErrHandler)
        {
            if (Directory.Exists(xcFrameworkPath))
            {
                Directory.Delete(xcFrameworkPath, true);
            }

            var startInfo = new ProcessStartInfo("xcrun");

            BuildXCFrameworkArguments(outputPaths, xcFrameworkPath, startInfo.ArgumentList);

            string launchMessage = startInfo.GetCommandLineDisplay(new StringBuilder("Packaging: ")).ToString();
            stdOutHandler?.Invoke(launchMessage);
            if (Godot.OS.IsStdOutVerbose())
                Console.WriteLine(launchMessage);

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;

            if (OperatingSystem.IsWindows())
            {
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }

            // Needed when running from Developer Command Prompt for VS.
            RemovePlatformVariable(startInfo.EnvironmentVariables);

            var process = new Process { StartInfo = startInfo };

            if (stdOutHandler != null)
                process.OutputDataReceived += (_, e) => stdOutHandler.Invoke(e.Data);
            if (stdErrHandler != null)
                process.ErrorDataReceived += (_, e) => stdErrHandler.Invoke(e.Data);

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }

        public static int GenerateXCFramework(List<string> outputPaths, string xcFrameworkPath, Action<string?>? stdOutHandler, Action<string?>? stdErrHandler)
        {
            using (var process = DoGenerateXCFramework(outputPaths, xcFrameworkPath, stdOutHandler, stdErrHandler))
            {
                process.WaitForExit();

                return process.ExitCode;
            }
        }

        private static void BuildXCFrameworkArguments(List<string> outputPaths,
            string xcFrameworkPath, Collection<string> arguments)
        {
            var baseDylib = $"{GodotSharpDirs.ProjectAssemblyName}.dylib";
            var baseSym = $"{GodotSharpDirs.ProjectAssemblyName}.framework.dSYM";

            arguments.Add("xcodebuild");
            arguments.Add("-create-xcframework");

            foreach (var outputPath in outputPaths)
            {
                arguments.Add("-library");
                arguments.Add(Path.Combine(outputPath, baseDylib));
                arguments.Add("-debug-symbols");
                arguments.Add(Path.Combine(outputPath, baseSym));
            }

            arguments.Add("-output");
            arguments.Add(xcFrameworkPath);
        }
    }
}
