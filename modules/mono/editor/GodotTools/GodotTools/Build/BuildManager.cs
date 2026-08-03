using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GodotTools.Internals;
using File = GodotTools.Utils.File;

namespace GodotTools.Build
{
    public static class BuildManager
    {
        private static BuildInfo? _buildInProgress;
        private static CancellationTokenSource? _activeBuildCancellation;

        public static bool BuildInProgress => _buildInProgress != null;

        // Conventional Godot owns the MSBuild dock. Embedded Xogot presents the
        // build UI in SwiftUI and consumes headless build-result snapshots.
        public static bool IsXogotEmbedded =>
            System.Environment.GetEnvironmentVariable("XOGOT_OPTIONAL_DOTNET_COMPONENT") == "1";

        public const string MsBuildIssuesFileName = "msbuild_issues.csv";
        private const string MsBuildLogFileName = "msbuild_log.txt";
        private const string MsBuildBinLogFileName = "msbuild.binlog";

        private sealed class BuildSnapshot
        {
            public long Id { get; init; }
            public BuildInfo BuildInfo { get; init; } = null!;
            public string Status { get; set; } = "building";
            public string FailureMessage { get; set; } = string.Empty;
            public List<BuildDiagnostic> Diagnostics { get; set; } = new();
        }

        private static readonly object _snapshotLock = new();
        private static long _nextBuildId;
        private static BuildSnapshot? _lastBuildSnapshot;

        private static void NotifyXogotBuildState(string state)
        {
            if (IsXogotEmbedded)
                Internal.XogotNotifyBuildState(state);
        }

        public delegate void BuildLaunchFailedEventHandler(BuildInfo buildInfo, string reason);

        public static event BuildLaunchFailedEventHandler? BuildLaunchFailed;
        public static event Action<BuildInfo>? BuildStarted;
        public static event Action<BuildResult>? BuildFinished;
        public static event Action<string?>? StdOutputReceived;
        public static event Action<string?>? StdErrorReceived;

        public static DateTime LastValidBuildDateTime { get; private set; }

        static BuildManager()
        {
            UpdateLastValidBuildDateTime();
        }

        public static void UpdateLastValidBuildDateTime()
        {
            var dllName = $"{GodotSharpDirs.ProjectAssemblyName}.dll";
            var path = Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, "Debug", dllName);
            LastValidBuildDateTime = File.GetLastWriteTime(path);
        }

        /// Returns whether the C# inputs are newer than the last assembly Godot accepted as valid.
        /// Xogot calls this before Play so unchanged projects can skip solution generation, MSBuild,
        /// and the OmniSharp restart that follows a real build.
        public static bool IsProjectBuildOutOfDate()
        {
            string projectPath = GodotSharpDirs.ProjectCsProjPath;
            string solutionPath = GodotSharpDirs.ProjectSlnPath;
            string projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
            string assemblyPath = Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, "Debug",
                $"{GodotSharpDirs.ProjectAssemblyName}.dll");
            if (!File.Exists(projectPath) || !File.Exists(solutionPath) || !System.IO.File.Exists(assemblyPath))
                return true;

            try
            {
                DateTime lastValidBuild = LastValidBuildDateTime;
                if (File.GetLastWriteTime(projectPath) > lastValidBuild ||
                    File.GetLastWriteTime(solutionPath) > lastValidBuild)
                    return true;

                foreach (string sourcePath in Directory.EnumerateFiles(projectDirectory, "*.cs",
                             SearchOption.AllDirectories))
                {
                    if (IsGeneratedBuildPath(projectDirectory, sourcePath))
                        continue;
                    if (System.IO.File.GetLastWriteTime(sourcePath) > lastValidBuild)
                        return true;
                }
                return false;
            }
            catch (IOException)
            {
                // A transient filesystem error must not let Play run stale managed code.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool IsGeneratedBuildPath(string projectDirectory, string path)
        {
            string relativePath = Path.GetRelativePath(projectDirectory, path);
            string[] components = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < components.Length - 1; index++)
            {
                if (components[index] is "bin" or "obj" or ".godot")
                    return true;
            }
            return false;
        }

        private static void RemoveOldIssuesFile(BuildInfo buildInfo)
        {
            string issuesFile = GetIssuesFilePath(buildInfo);

            if (!File.Exists(issuesFile))
                return;

            File.Delete(issuesFile);
        }

        private static void ShowBuildErrorDialog(string message)
        {
            if (IsXogotEmbedded)
                return;

            var plugin = GodotSharpEditor.Instance;
            plugin.ShowErrorDialog(message, "Build error");
            plugin.MSBuildPanel?.MakeVisible();
        }

        private static string GetLogFilePath(BuildInfo buildInfo)
        {
            return Path.Combine(buildInfo.LogsDirPath, MsBuildLogFileName);
        }

        private static string GetIssuesFilePath(BuildInfo buildInfo)
        {
            return Path.Combine(buildInfo.LogsDirPath, MsBuildIssuesFileName);
        }

        private static void BeginBuildSnapshot(BuildInfo buildInfo)
        {
            lock (_snapshotLock)
            {
                _lastBuildSnapshot = new BuildSnapshot
                {
                    Id = Interlocked.Increment(ref _nextBuildId),
                    BuildInfo = buildInfo,
                };
            }
            NotifyXogotBuildState("building");
        }

        private static List<BuildDiagnostic> ReadDiagnostics(BuildInfo buildInfo)
        {
            var diagnostics = new List<BuildDiagnostic>();
            using var file = Godot.FileAccess.Open(GetIssuesFilePath(buildInfo), Godot.FileAccess.ModeFlags.Read);
            if (file == null)
                return diagnostics;

            while (!file.EofReached())
            {
                string[] columns = file.GetCsvLine();
                if (columns.Length == 1 && string.IsNullOrEmpty(columns[0]))
                    break;
                if (columns.Length != 7)
                    continue;

                _ = int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int line);
                _ = int.TryParse(columns[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int column);
                diagnostics.Add(new BuildDiagnostic
                {
                    Type = columns[0] == "warning"
                        ? BuildDiagnostic.DiagnosticType.Warning
                        : BuildDiagnostic.DiagnosticType.Error,
                    File = columns[1],
                    Line = line,
                    Column = column,
                    Code = columns[4],
                    Message = columns[5],
                    ProjectFile = columns[6],
                });
            }

            return diagnostics;
        }

        private static void CompleteBuildSnapshot(BuildInfo buildInfo, bool success, string failureMessage = "")
        {
            var diagnostics = ReadDiagnostics(buildInfo);
            if (!success && diagnostics.Count == 0)
            {
                diagnostics.Add(new BuildDiagnostic
                {
                    Type = BuildDiagnostic.DiagnosticType.Error,
                    Message = string.IsNullOrEmpty(failureMessage)
                        ? $"The .NET build failed. See {GetLogFilePath(buildInfo)} for details."
                        : failureMessage,
                });
            }

            lock (_snapshotLock)
            {
                if (_lastBuildSnapshot?.BuildInfo != buildInfo)
                    return;
                _lastBuildSnapshot.Status = success ? "success" :
                    string.IsNullOrEmpty(failureMessage) ? "failed" : "launch_failed";
                _lastBuildSnapshot.FailureMessage = failureMessage;
                _lastBuildSnapshot.Diagnostics = diagnostics;
            }
            NotifyXogotBuildState(success ? "success" : "failed");
        }

        // MSBuild can exit before Godot has loaded the new project assembly. Xogot
        // must keep Play blocked through that native reload, otherwise it can start
        // the previous assembly or an assembly that has no registered script types.
        private static void MarkBuildSnapshotReloading(BuildInfo buildInfo)
        {
            lock (_snapshotLock)
            {
                if (_lastBuildSnapshot?.BuildInfo != buildInfo)
                    return;
                _lastBuildSnapshot.Status = "reloading";
                _lastBuildSnapshot.FailureMessage = string.Empty;
                _lastBuildSnapshot.Diagnostics = ReadDiagnostics(buildInfo);
            }
            NotifyXogotBuildState("reloading");
        }

        /// Completes an Xogot build after Godot has accepted the project assembly.
        /// The native reload callback invokes this method on both success and failure.
        public static void CompleteXogotBuildAfterAssemblyReload(bool success, string failureMessage = "")
        {
            lock (_snapshotLock)
            {
                if (_lastBuildSnapshot?.Status != "reloading")
                    return;

                BuildInfo buildInfo = _lastBuildSnapshot.BuildInfo;
                _lastBuildSnapshot.Status = success ? "success" : "launch_failed";
                _lastBuildSnapshot.FailureMessage = failureMessage;
                _lastBuildSnapshot.Diagnostics = ReadDiagnostics(buildInfo);
                if (!success && _lastBuildSnapshot.Diagnostics.Count == 0)
                {
                    _lastBuildSnapshot.Diagnostics.Add(new BuildDiagnostic
                    {
                        Type = BuildDiagnostic.DiagnosticType.Error,
                        Message = string.IsNullOrEmpty(failureMessage)
                            ? "Godot failed to load the built project assembly."
                            : failureMessage,
                    });
                }
            }
            NotifyXogotBuildState(success ? "success" : "launch_failed");
        }

        private static void CancelBuildSnapshot(BuildInfo buildInfo)
        {
            lock (_snapshotLock)
            {
                if (_lastBuildSnapshot?.BuildInfo != buildInfo)
                    return;
                _lastBuildSnapshot.Status = "cancelled";
                _lastBuildSnapshot.FailureMessage = string.Empty;
                _lastBuildSnapshot.Diagnostics = ReadDiagnostics(buildInfo);
            }
            NotifyXogotBuildState("cancelled");
        }

        /// Requests cancellation of the build currently started through the Xogot bridge.
        /// BuildSystem sends SIGINT first, then kills the process tree after its grace period.
        public static bool CancelBuild()
        {
            CancellationTokenSource? cancellation = _activeBuildCancellation;
            if (cancellation == null || cancellation.IsCancellationRequested)
                return false;
            cancellation.Cancel();
            return true;
        }

        public static Godot.Collections.Dictionary GetLastBuildResult()
        {
            lock (_snapshotLock)
            {
                if (_lastBuildSnapshot == null)
                    return new Godot.Collections.Dictionary();

                BuildSnapshot snapshot = _lastBuildSnapshot;
                BuildInfo buildInfo = snapshot.BuildInfo;
                var diagnostics = new Godot.Collections.Array<Godot.Collections.Dictionary>();
                foreach (BuildDiagnostic diagnostic in snapshot.Diagnostics)
                {
                    diagnostics.Add(new Godot.Collections.Dictionary
                    {
                        ["severity"] = diagnostic.Type == BuildDiagnostic.DiagnosticType.Warning ? "warning" : "error",
                        ["file"] = diagnostic.File ?? string.Empty,
                        ["line"] = diagnostic.Line,
                        ["column"] = diagnostic.Column,
                        ["code"] = diagnostic.Code ?? string.Empty,
                        ["message"] = diagnostic.Message,
                        ["project"] = diagnostic.ProjectFile ?? string.Empty,
                    });
                }

                return new Godot.Collections.Dictionary
                {
                    ["build_id"] = snapshot.Id,
                    ["status"] = snapshot.Status,
                    ["configuration"] = buildInfo.Configuration,
                    ["project"] = buildInfo.Project,
                    ["solution"] = buildInfo.Solution,
                    ["log"] = GetLogFilePath(buildInfo),
                    ["binlog"] = Path.Combine(buildInfo.LogsDirPath, MsBuildBinLogFileName),
                    ["issues"] = GetIssuesFilePath(buildInfo),
                    ["failure_message"] = snapshot.FailureMessage,
                    ["diagnostics"] = diagnostics,
                };
            }
        }

        private static void PrintVerbose(string text)
        {
            if (OS.IsStdOutVerbose())
                GD.Print(text);
        }

        private static void ReportXogotBuildFailure(BuildInfo buildInfo, int exitCode)
        {
            if (!IsXogotEmbedded)
                return;

            string logPath = GetLogFilePath(buildInfo);
            // PrintErr enters EditorLog as normal stderr output. PushError would
            // produce a managed stack trace for every compiler diagnostic.
            GD.PrintErr($"[.NET] Build failed (exit code {exitCode}). MSBuild log: {logPath}");

            if (!File.Exists(logPath))
                return;

            try
            {
                int reportedErrors = 0;
                foreach (string line in System.IO.File.ReadLines(logPath))
                {
                    if (line.IndexOf(": error", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    GD.PrintErr($"[.NET] MSBuild: {line}");
                    if (++reportedErrors == 8)
                        break;
                }
            }
            catch (IOException)
            {
                // The logger may still be flushing when the process exits. The
                // path printed above remains available to diagnose the failure.
            }
        }

        private static bool Build(BuildInfo buildInfo)
        {
            if (_buildInProgress != null)
                throw new InvalidOperationException("A build is already in progress.");

            _buildInProgress = buildInfo;

            try
            {
                BeginBuildSnapshot(buildInfo);
                BuildStarted?.Invoke(buildInfo);

                // The native dock must not be driven synchronously by Xogot.
                // Its SwiftUI frontend receives the build diagnostics through
                // EditorLog after this call returns to the main loop.
                if (!IsXogotEmbedded)
                    Internal.GodotMainIteration();

                try
                {
                    RemoveOldIssuesFile(buildInfo);
                }
                catch (IOException e)
                {
                    BuildLaunchFailed?.Invoke(buildInfo, $"Cannot remove issues file: {GetIssuesFilePath(buildInfo)}");
                    Console.Error.WriteLine(e);
                }

                try
                {
                    int exitCode = BuildSystem.Build(buildInfo, StdOutputReceived, StdErrorReceived);

                    if (exitCode != 0)
                    {
                        PrintVerbose($"MSBuild exited with code: {exitCode}. Log file: {GetLogFilePath(buildInfo)}");
                        ReportXogotBuildFailure(buildInfo, exitCode);
                    }

                    CompleteBuildSnapshot(buildInfo, exitCode == 0);

                    BuildFinished?.Invoke(exitCode == 0 ? BuildResult.Success : BuildResult.Error);

                    return exitCode == 0;
                }
                catch (Exception e)
                {
                    string failureMessage =
                        $"The build method threw an exception.\n{e.GetType().FullName}: {e.Message}";
                    CompleteBuildSnapshot(buildInfo, success: false, failureMessage: failureMessage);
                    BuildLaunchFailed?.Invoke(buildInfo,
                        failureMessage);
                    Console.Error.WriteLine(e);
                    return false;
                }
            }
            finally
            {
                _buildInProgress = null;
            }
        }

        public static async Task<bool> BuildAsync(BuildInfo buildInfo, bool holdSuccessfulSnapshotForAssemblyReload = false)
        {
            if (_buildInProgress != null)
                throw new InvalidOperationException("A build is already in progress.");

            _buildInProgress = buildInfo;
            using var cancellation = new CancellationTokenSource();
            _activeBuildCancellation = cancellation;

            try
            {
                BeginBuildSnapshot(buildInfo);
                BuildStarted?.Invoke(buildInfo);

                try
                {
                    RemoveOldIssuesFile(buildInfo);
                }
                catch (IOException e)
                {
                    BuildLaunchFailed?.Invoke(buildInfo, $"Cannot remove issues file: {GetIssuesFilePath(buildInfo)}");
                    Console.Error.WriteLine(e);
                }

                try
                {
                    int exitCode = await BuildSystem.BuildAsync(
                        buildInfo, StdOutputReceived, StdErrorReceived, cancellation.Token);

                    if (cancellation.IsCancellationRequested)
                    {
                        CancelBuildSnapshot(buildInfo);
                        BuildFinished?.Invoke(BuildResult.Error);
                        return false;
                    }

                    if (exitCode != 0)
                    {
                        PrintVerbose($"MSBuild exited with code: {exitCode}. Log file: {GetLogFilePath(buildInfo)}");
                        ReportXogotBuildFailure(buildInfo, exitCode);
                    }

                    if (exitCode == 0 && holdSuccessfulSnapshotForAssemblyReload)
                        MarkBuildSnapshotReloading(buildInfo);
                    else
                        CompleteBuildSnapshot(buildInfo, exitCode == 0);

                    BuildFinished?.Invoke(exitCode == 0 ? BuildResult.Success : BuildResult.Error);

                    return exitCode == 0;
                }
                catch (Exception e)
                {
                    string failureMessage =
                        $"The build method threw an exception.\n{e.GetType().FullName}: {e.Message}";
                    CompleteBuildSnapshot(buildInfo, success: false, failureMessage: failureMessage);
                    BuildLaunchFailed?.Invoke(buildInfo,
                        failureMessage);
                    Console.Error.WriteLine(e);
                    return false;
                }
            }
            catch (Exception e)
            {
                // BeginBuildSnapshot happens before BuildStarted, so even a throwing subscriber
                // has a terminal result for Xogot's asynchronous SwiftUI coordinator to consume.
                // Without this boundary the bridge sees `building` forever and cannot be stopped.
                string failureMessage =
                    $"The build setup threw an exception.\n{e.GetType().FullName}: {e.Message}";
                CompleteBuildSnapshot(buildInfo, success: false, failureMessage: failureMessage);
                try
                {
                    BuildLaunchFailed?.Invoke(buildInfo, failureMessage);
                }
                catch (Exception eventException)
                {
                    Console.Error.WriteLine(eventException);
                }
                Console.Error.WriteLine(e);
                return false;
            }
            finally
            {
                if (ReferenceEquals(_activeBuildCancellation, cancellation))
                    _activeBuildCancellation = null;
                _buildInProgress = null;
            }
        }

        private static bool Publish(BuildInfo buildInfo)
        {
            if (_buildInProgress != null)
                throw new InvalidOperationException("A build is already in progress.");

            _buildInProgress = buildInfo;

            try
            {
                BeginBuildSnapshot(buildInfo);
                BuildStarted?.Invoke(buildInfo);

                if (!IsXogotEmbedded)
                    Internal.GodotMainIteration();

                try
                {
                    RemoveOldIssuesFile(buildInfo);
                }
                catch (IOException e)
                {
                    BuildLaunchFailed?.Invoke(buildInfo, $"Cannot remove issues file: {GetIssuesFilePath(buildInfo)}");
                    Console.Error.WriteLine(e);
                }

                try
                {
                    int exitCode = BuildSystem.Publish(buildInfo, StdOutputReceived, StdErrorReceived);

                    if (exitCode != 0)
                        PrintVerbose(
                            $"dotnet publish exited with code: {exitCode}. Log file: {GetLogFilePath(buildInfo)}");

                    CompleteBuildSnapshot(buildInfo, exitCode == 0);

                    BuildFinished?.Invoke(exitCode == 0 ? BuildResult.Success : BuildResult.Error);

                    return exitCode == 0;
                }
                catch (Exception e)
                {
                    string failureMessage =
                        $"The publish method threw an exception.\n{e.GetType().FullName}: {e.Message}";
                    CompleteBuildSnapshot(buildInfo, success: false, failureMessage: failureMessage);
                    BuildLaunchFailed?.Invoke(buildInfo,
                        failureMessage);
                    Console.Error.WriteLine(e);
                    return false;
                }
            }
            finally
            {
                _buildInProgress = null;
            }
        }

        private static bool BuildProjectBlocking(BuildInfo buildInfo)
        {
            if (!File.Exists(buildInfo.Project))
                return true; // No project to build.

            bool success;
            using (var pr = new EditorProgress("dotnet_build_project", "Building .NET project...", 1))
            {
                pr.Step("Building project", 0);
                success = Build(buildInfo);
            }

            if (!success)
            {
                ShowBuildErrorDialog("Failed to build project. Check MSBuild panel for details.");
            }

            return success;
        }

        private static bool CleanProjectBlocking(BuildInfo buildInfo)
        {
            if (!File.Exists(buildInfo.Project))
                return true; // No project to clean.

            bool success;
            using (var pr = new EditorProgress("dotnet_clean_project", "Cleaning .NET project...", 1))
            {
                pr.Step("Cleaning project", 0);
                success = Build(buildInfo);
            }

            if (!success)
            {
                ShowBuildErrorDialog("Failed to clean project");
            }

            return success;
        }

        private static bool PublishProjectBlocking(BuildInfo buildInfo)
        {
            bool success;
            using (var pr = new EditorProgress("dotnet_publish_project", "Publishing .NET project...", 1))
            {
                pr.Step("Running dotnet publish", 0);
                success = Publish(buildInfo);
            }

            return success;
        }

        private static BuildInfo CreateBuildInfo(
            string configuration,
            string? platform = null,
            bool rebuild = false,
            bool onlyClean = false
        )
        {
            var buildInfo = new BuildInfo(GodotSharpDirs.ProjectSlnPath, GodotSharpDirs.ProjectCsProjPath, configuration,
                restore: true, rebuild, onlyClean);

            // If a platform was not specified, try determining the current one. If that fails, let MSBuild auto-detect it.
            if (platform != null || Utils.OS.PlatformNameMap.TryGetValue(OS.GetName(), out platform))
                buildInfo.CustomProperties.Add($"GodotTargetPlatform={platform}");

            if (Internal.GodotIsRealTDouble())
                buildInfo.CustomProperties.Add("GodotFloat64=true");

            return buildInfo;
        }

        private static BuildInfo CreatePublishBuildInfo(
            string configuration,
            string platform,
            string runtimeIdentifier,
            string publishOutputDir,
            bool includeDebugSymbols = true
        )
        {
            var buildInfo = new BuildInfo(GodotSharpDirs.ProjectSlnPath, GodotSharpDirs.ProjectCsProjPath, configuration,
                runtimeIdentifier, publishOutputDir, restore: true, rebuild: false, onlyClean: false);

            if (!includeDebugSymbols)
            {
                buildInfo.CustomProperties.Add("DebugType=None");
                buildInfo.CustomProperties.Add("DebugSymbols=false");
            }

            buildInfo.CustomProperties.Add($"GodotTargetPlatform={platform}");

            if (Internal.GodotIsRealTDouble())
                buildInfo.CustomProperties.Add("GodotFloat64=true");

            return buildInfo;
        }

        public static bool BuildProjectBlocking(
            string configuration,
            string? platform = null,
            bool rebuild = false
        ) => BuildProjectBlocking(CreateBuildInfo(configuration, platform, rebuild));

        public static async Task<bool> BuildProjectAsync(
            string configuration,
            string? platform = null,
            bool rebuild = false,
            bool holdSuccessfulSnapshotForAssemblyReload = false
        )
        {
            BuildInfo buildInfo = CreateBuildInfo(configuration, platform, rebuild);
            if (!File.Exists(buildInfo.Project))
            {
                // Preserve the established "nothing to build" success result, but always
                // publish a terminal snapshot for the asynchronous Xogot bridge.
                BeginBuildSnapshot(buildInfo);
                if (holdSuccessfulSnapshotForAssemblyReload)
                    MarkBuildSnapshotReloading(buildInfo);
                else
                    CompleteBuildSnapshot(buildInfo, success: true);
                return true;
            }

            bool success = await BuildAsync(buildInfo, holdSuccessfulSnapshotForAssemblyReload);
            if (!success && !IsXogotEmbedded)
                ShowBuildErrorDialog("Failed to build project. Check MSBuild panel for details.");
            return success;
        }

        public static bool CleanProjectBlocking(
            string configuration,
            string? platform = null
        ) => CleanProjectBlocking(CreateBuildInfo(configuration, platform, rebuild: false, onlyClean: true));

        public static bool PublishProjectBlocking(
            string configuration,
            string platform,
            string runtimeIdentifier,
            string publishOutputDir,
            bool includeDebugSymbols = true
        ) => PublishProjectBlocking(CreatePublishBuildInfo(configuration,
            platform, runtimeIdentifier, publishOutputDir, includeDebugSymbols));

        public static bool GenerateXCFrameworkBlocking(
            List<string> outputPaths,
            string xcFrameworkPath)
        {
            using var pr = new EditorProgress("generate_xcframework", "Generating XCFramework...", 1);

            pr.Step("Running xcodebuild -create-xcframework", 0);

            if (!GenerateXCFramework(outputPaths, xcFrameworkPath))
            {
                ShowBuildErrorDialog("Failed to generate XCFramework");
                return false;
            }

            return true;
        }

        private static bool GenerateXCFramework(List<string> outputPaths, string xcFrameworkPath)
        {
            if (!IsXogotEmbedded)
                Internal.GodotMainIteration();

            try
            {
                int exitCode = BuildSystem.GenerateXCFramework(outputPaths, xcFrameworkPath, StdOutputReceived, StdErrorReceived);

                if (exitCode != 0)
                    PrintVerbose(
                        $"xcodebuild create-xcframework exited with code: {exitCode}.");

                return exitCode == 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return false;
            }
        }

        public static bool EditorBuildCallback()
        {
            if (!File.Exists(GodotSharpDirs.ProjectCsProjPath))
                return true; // No project to build.

            if (GodotSharpEditor.Instance.SkipBuildBeforePlaying)
            {
                // The Xogot/IDE launch marker is deliberately one-shot. A later Play must always
                // rebuild unless its own preflight compilation completed successfully.
                GodotSharpEditor.Instance.SkipBuildBeforePlaying = false;
                return true;
            }

            return BuildProjectBlocking("Debug");
        }

        public static void Initialize()
        {
        }
    }
}
