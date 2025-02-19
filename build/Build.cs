using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;

partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.BuildTracer);

    [Parameter("Configuration to build. Default is 'Release'")]
    readonly Configuration BuildConfiguration = Configuration.Release;

    [Parameter("Platform to build - x86 or x64. Default is 'x64'")]
    readonly MSBuildTargetPlatform Platform = MSBuildTargetPlatform.x64;

    [Parameter($"Docker containers type to be used. One of '{ContainersNone}', '{ContainersLinux}', '{ContainersWindows}'. Default is '{ContainersLinux}'")]
    readonly string Containers = ContainersLinux;

    const string ContainersNone = "none";
    const string ContainersAny = "any";
    const string ContainersLinux = "linux";
    const string ContainersWindows = "windows";

    [Parameter("Test projects filter. Optional, default matches all test projects. The project will be selected if the string is part of its name.")]
    readonly string TestProject = "";

    [Parameter("Test name filter. Optional")]
    readonly string TestName;

    [Parameter("Number of times each dotnet test is run. Default is '1'")]
    readonly int TestCount = 1;

    [Parameter("Windows Server Core container version. Use it if your Windows does not support the default value. Default is 'ltsc2022'")]
    readonly string WindowsContainerVersion = "ltsc2022";

    [Parameter("The location to create the tracer home directory. Default is './bin/tracer-home'")]
    readonly AbsolutePath TracerHome;

    [Parameter("The location to place the NuGet packages built from the project. Default is './bin/nuget-artifacts'")]
    readonly AbsolutePath NuGetArtifacts;

    [Parameter("The location to restore NuGet packages. Optional")]
    readonly AbsolutePath NuGetPackagesDirectory;

    [Parameter("Version number of the NuGet packages built from the project. Default is '0.7.0'")]
    string NuGetBaseVersionNumber = "0.7.0";

    [Parameter("Version suffix added to the NuGet packages built from the project. Default is '-local.1'")]
    string NuGetVersionSuffix = "-local.1";

    Target Clean => _ => _
        .Description("Cleans all build output")
        .Executes(() =>
        {
            if (IsWin)
            {
                // These are created as part of the CreatePlatformlessSymlinks target and cause havok
                // when deleting directories otherwise
                DeleteReparsePoints(SourceDirectory);
                DeleteReparsePoints(TestsDirectory);
            }
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
            OutputDirectory.CreateOrCleanDirectory();
            TracerHomeDirectory.CreateOrCleanDirectory();
            NuGetArtifactsDirectory.CreateOrCleanDirectory();
            (NativeProfilerProject.Directory / "build").CreateOrCleanDirectory();
            (NativeProfilerProject.Directory / "deps").CreateOrCleanDirectory();
            BuildDataDirectory.CreateOrCleanDirectory();

            void DeleteReparsePoints(string path)
            {
                new DirectoryInfo(path)
                   .GetDirectories("*", SearchOption.AllDirectories)
                   .Where(x => x.Attributes.HasFlag(FileAttributes.ReparsePoint))
                   .ForEach(dir => Cmd.Value(arguments: $"cmd /c rmdir \"{dir}\""));
            }
        });

    Target Workflow => _ => _
        .Description("GitHub workflow entry point")
        .DependsOn(Clean)
        .DependsOn(BuildTracer)
        .DependsOn(CompileExamples)
        .DependsOn(NativeTests)
        .DependsOn(ManagedTests);

    Target BuildTracer => _ => _
        .Description("Builds the native and managed src, and publishes the tracer home directory")
        .After(Clean)
        .DependsOn(CreateRequiredDirectories)
        .DependsOn(Restore)
        .DependsOn(GenerateNetFxTransientDependencies)
        .DependsOn(CompileManagedSrc)
        .DependsOn(PublishManagedProfiler)
        .DependsOn(PublishRuleEngineJson)
        .DependsOn(GenerateNetFxAssemblyRedirectionSource)
        .DependsOn(CompileNativeSrc)
        .DependsOn(PublishNativeProfiler)
        .DependsOn(CopyInstrumentScripts)
        .DependsOn(CopyLegalFiles);

    Target NativeTests => _ => _
        .Description("Builds the native unit tests and runs them")
        .After(Clean, BuildTracer)
        .DependsOn(CreateRequiredDirectories)
        .DependsOn(CompileNativeTests)
        .DependsOn(RunNativeTests);

    Target ManagedTests => _ => _
        .Description("Builds the managed unit / integration tests and runs them")
        .After(Clean, BuildTracer)
        .DependsOn(CreateRequiredDirectories)
        .DependsOn(GenerateLibraryVersionFiles)
        .DependsOn(CompileManagedTests)
        .DependsOn(CompileMocks)
        .DependsOn(PublishMocks)
        .DependsOn(PublishIisTestApplications)
        .DependsOn(InstallNetFxAssembliesGAC)
        .DependsOn(RunManagedTests);

    string ContainersFilter()
    {
        switch (Containers)
        {
            case ContainersNone:
                return "Containers!=Linux&Containers!=Windows&Containers!=Any";
            case ContainersLinux:
                return "Containers!=Windows";
            case ContainersWindows:
                return "Containers!=Linux";
            case ContainersAny:
                throw new InvalidOperationException($"Containers={ContainersAny} is not supported directly. Specify concrete value, either Containers={ContainersLinux} or Containers={ContainersWindows}.");
            default:
                throw new InvalidOperationException($"Containers={Containers} is not supported");
        }
    }

    string TestNameFilter()
    {
        if (string.IsNullOrEmpty(TestName))
        {
            return null;
        }

        return "FullyQualifiedName~" + TestName;
    }

    static string AndFilter(params string[] args)
    {
        return string.Join("&", args.Where(s => !string.IsNullOrEmpty(s)));
    }
}
