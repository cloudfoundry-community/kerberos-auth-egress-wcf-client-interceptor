using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using Octokit;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("ApiKey for the specified source.")]
    readonly string SourceApiKey;

    [Parameter("GitHub personal access token with access to the repo")]
    string GithubApiKey;

    const string nugetSourceUrl = @"https://api.nuget.org/v3/index.json";
    const string mygetSourceUrl = @"https://www.myget.org/F/ajaganathan/api/v2/package";

    [Parameter("Source url for the nuget/myget repository")]
    readonly string Source = mygetSourceUrl;

    [Solution]
    readonly Solution Solution;

    [GitVersion]
    readonly GitVersion GitVersion;

    [GitRepository]
    readonly GitRepository GitRepository;


    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ProjectDirectory => SourceDirectory / "RouteServiceIwaWcfInterceptor";
    AbsolutePath ProjectFile => ProjectDirectory / "RouteServiceIwaWcfInterceptor.csproj";
    AbsolutePath NuspecFile => ProjectDirectory / "RouteServiceIwaWcfInterceptor.nuspec";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    Target Clean => _ => _
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            //TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteFile);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            MSBuild(s => s
                .SetTargetPath(Solution)
                .SetTargets("Restore"));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            MSBuild(s => s
                .SetTargetPath(Solution)
                .SetTargets("Rebuild")
                .SetConfiguration(Configuration)
                //.SetAssemblyVersion(GitVersion.GetNormalizedAssemblyVersion())
                .SetAssemblyVersion("1.0.0.0") //For the sake of buildpack configuring behaviour extension
                .SetFileVersion(GitVersion.GetNormalizedFileVersion())
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .SetMaxCpuCount(Environment.ProcessorCount)
                .SetNodeReuse(IsLocalBuild));
        });

    Target Pack => _ => _
    .DependsOn(Compile)
    .Executes(() =>
    {
        var preReleaseTag = Source.Contains("nuget") ? "beta" : "alpha";
        RunProcess("nuget.exe", $"pack {ProjectFile} -Version {GitVersion.MajorMinorPatch}-{preReleaseTag} -OutputDirectory {ArtifactsDirectory} -Properties Configuration={Configuration}");
    });

    Target Push => _ => _
    .DependsOn(Pack)
    .Requires(() => Source)
    .Requires(() => Configuration)
    .Requires(() => SourceApiKey)
    .Requires(() => GithubApiKey)
    .Executes(() =>
    {
        GlobFiles(ArtifactsDirectory, "*.nupkg").NotEmpty()
            .Where(artifactFullPath => !artifactFullPath.EndsWith(".symbols.nupkg"))
            .ForEach(artifactFullPath =>
            {
                if (Path.IsPathRooted(Source))
                {
                    throw new Exception($"Source should be a nuget or myget url for executing target 'Push'");
                }
                else
                {
                    ReleaseInGitHub(artifactFullPath);

                    Logger.Log(LogLevel.Normal, $"Pushing to nuget source {Source}");

                    DotNetTasks.DotNetNuGetPush(s => s
                        .SetApiKey(SourceApiKey)
                        .SetTargetPath(artifactFullPath)
                        .SetSource(Source));

                    Logger.Log(LogLevel.Normal, $"Pushed to nuget source {Source} completed successfully");
                }
            });
    });

    Target Add => _ => _
   .DependsOn(Pack)
   .Requires(() => Source)
   .Executes(() =>
   {
       GlobFiles(ArtifactsDirectory, "*.nupkg").NotEmpty()
           .Where(artifactFullPath => !artifactFullPath.EndsWith(".symbols.nupkg"))
           .ForEach(artifactFullPath =>
           {
               if (Path.IsPathRooted(Source))
               {
                   RunProcess("nuget.exe", $"add {Path.Combine(ArtifactsDirectory, artifactFullPath)} -Source {Source}");
               }
               else
               {
                   throw new Exception($"Source should be an absolute path for executing target 'Add'");
               }
           });
   });

    private void ReleaseInGitHub(string artifactFullPath)
    {

        if (!GitRepository.IsGitHubRepository())
            throw new Exception("Only supported when git repo remote is github");

        var preReleaseTag = Source.Contains("nuget") ? "beta" : "alpha";

        var packageName = Path.GetFileName(artifactFullPath);

        var client = new GitHubClient(new ProductHeaderValue(Path.GetFileNameWithoutExtension(artifactFullPath)))
        {
            Credentials = new Credentials(GithubApiKey, AuthenticationType.Bearer)
        };

        Logger.Log(LogLevel.Normal, $"Releasing in Github {client.BaseAddress}");

        var gitIdParts = GitRepository.Identifier.Split("/");
        var owner = gitIdParts[0];
        var repoName = gitIdParts[1];

        var releaseName = $"v{GitVersion.MajorMinorPatch}-{preReleaseTag}";

        Release release;
        try
        {
            Logger.Log(LogLevel.Normal, $"Checking for existence of release with name {releaseName}...");

            release = client.Repository.Release.Get(owner, repoName, releaseName).Result;

            Logger.Log(LogLevel.Normal, $"Found release {releaseName} at {release.AssetsUrl}");
        }
        catch (Exception)
        {
            Logger.Log(LogLevel.Normal, $"Release with name {releaseName} not found.. so creating new...");

            var newRelease = new NewRelease(releaseName)
            {
                Name = releaseName,
                Draft = false,
                Prerelease = false
            };
            release = client.Repository.Release.Create(owner, repoName, newRelease).Result;
        }

        var existingAsset = release.Assets.FirstOrDefault(y => y.Name == packageName);

        if (existingAsset != null)
        {
            Logger.Log(LogLevel.Normal, $"Deleting assert {existingAsset.Name}...");

            client.Repository.Release.DeleteAsset(owner, repoName, existingAsset.Id);
        }

        Logger.Log(LogLevel.Normal, $"Uploading assert {existingAsset.Name}...");

        var releaseAssetUpload = new ReleaseAssetUpload(packageName, "application/zip", File.OpenRead(artifactFullPath), null);
        var releaseAsset = client.Repository.Release.UploadAsset(release, releaseAssetUpload).Result;

        Logger.Block(releaseAsset.BrowserDownloadUrl);

        Logger.Log(LogLevel.Normal, $"Released in Github {client.BaseAddress}, successfully");
    }

    private void RunProcess(string processFullName, string argument)
    {
        var startInfo = new ProcessStartInfo(processFullName)
        {
            Arguments = argument,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var process = Process.Start(startInfo);
        var result = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        if (!string.IsNullOrWhiteSpace(error))
            throw new Exception(error);

        Console.Write(result);

        process.WaitForExit();
    }
}
