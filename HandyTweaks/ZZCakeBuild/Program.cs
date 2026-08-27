using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Frosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace CakeBuild
{
    public static class Program
    {
        public static int Main(string[] args) =>
            new CakeHost().UseContext<BuildContext>().Run(args);
    }

    public class BuildContext : FrostingContext
    {
        public const string DefaultProjectName = "HandyTweaks";

        public string BuildConfiguration { get; }
        public string[] ProjectPaths { get; }
        public (string Label, string Tfm)[] Targets { get; } =
        {
            ("VS1.22","net10.0")
        };

        public string VS122 { get; }
        public string VS_Fallback { get; }

        public string VS122_VersionOverride { get; }

        public BuildContext(ICakeContext ctx) : base(ctx)
        {
            BuildConfiguration = ctx.Argument("configuration", "Release");

            var projArg = ctx.Argument("project", (string)null);

            if (!string.IsNullOrWhiteSpace(projArg))
            {
                ProjectPaths = projArg
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Trim('"'))
                    .ToArray();
            }
            else
            {
                var found = ctx.GetFiles("../*/**/*.csproj")
                    .Select(f => f.FullPath)
                    .Where(p => File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "modinfo.json")))
                    .ToArray();

                ProjectPaths = found.Length > 0
                    ? found
                    : new[] { $"../{DefaultProjectName}/{DefaultProjectName}.csproj" };
            }

            VS122 = ctx.EnvironmentVariable("VS122");
            VS_Fallback = ctx.EnvironmentVariable("VINTAGE_STORY");

            VS122_VersionOverride = ctx.Argument("vs122ver", (string)null) ?? ctx.EnvironmentVariable("VS122_MODVER");
        }
    }

    [TaskName("ValidateJson")]
    public sealed class ValidateJsonTask : FrostingTask<BuildContext>
    {
        public override void Run(BuildContext ctx)
        {
            var skip = ctx.Argument("skipJsonValidation", false);
            if (skip) return;

            foreach (var proj in ctx.ProjectPaths)
            {
                var projectRoot = Path.GetFullPath(Path.Combine(proj, ".."));
                var jsonFiles = ctx.GetFiles($"{projectRoot}/assets/**/*.json");
                foreach (var file in jsonFiles)
                {
                    try { JToken.Parse(File.ReadAllText(file.FullPath)); }
                    catch (JsonException ex)
                    {
                        throw new Exception($"JSON validation failed: {file.FullPath}{Environment.NewLine}{ex.Message}", ex);
                    }
                }
            }
        }
    }

    [TaskName("Build")]
    [IsDependentOn(typeof(ValidateJsonTask))]
    public sealed class BuildTask : FrostingTask<BuildContext>
    {
        public override void Run(BuildContext ctx)
        {
            foreach (var proj in ctx.ProjectPaths)
            {
                ctx.Information($"Cleaning {proj} …");
                ctx.DotNetClean(proj, new DotNetCleanSettings
                {
                    Configuration = ctx.BuildConfiguration
                });

                foreach (var (label, tfm) in ctx.Targets)
                {
                    var vs122 = ctx.VS122 ?? ctx.VS_Fallback ?? "";

                    ctx.Information($"Publishing {proj} → {label} ({tfm}) …");
                    ctx.DotNetPublish(proj, new DotNetPublishSettings
                    {
                        Configuration = ctx.BuildConfiguration,
                        Framework = tfm,
                        ArgumentCustomization = args => args
                            .Append($"/p:VS122=\"{vs122}\"")
                    });
                }
            }
        }
    }

    [TaskName("Package")]
    [IsDependentOn(typeof(BuildTask))]
    public sealed class PackageTask : FrostingTask<BuildContext>
    {
        public override void Run(BuildContext ctx)
        {
            ctx.EnsureDirectoryExists("../Releases");

            foreach (var proj in ctx.ProjectPaths)
            {
                var projectRoot = Path.GetFullPath(Path.Combine(proj, ".."));
                var baseModInfoPath = Path.Combine(projectRoot, "modinfo.json");
                if (!File.Exists(baseModInfoPath))
                    throw new FileNotFoundException($"modinfo.json not found next to project: {proj}");

                var baseJson = JObject.Parse(File.ReadAllText(baseModInfoPath));
                var modId = baseJson["modid"]?.Value<string>() ?? Path.GetFileNameWithoutExtension(proj);
                var version = ctx.VS122_VersionOverride
                    ?? baseJson["version"]?.Value<string>()
                    ?? "1.0.0";

                foreach (var (label, tfm) in ctx.Targets)
                {
                    var publishDir = Path.Combine(projectRoot, $"bin/{ctx.BuildConfiguration}/Mods/mod/{tfm}/publish");
                    if (!ctx.DirectoryExists(publishDir))
                        throw new DirectoryNotFoundException($"Publish dir not found: {publishDir}");

                    var releasesRoot = "../Releases";
                    var outDir = Path.Combine(releasesRoot, $"{modId}-{label}");
                    ctx.CleanDirectory(outDir);
                    ctx.EnsureDirectoryExists(outDir);

                    ctx.CopyDirectory(publishDir, outDir);

                    var assetsDir = Path.Combine(projectRoot, "assets");
                    if (ctx.DirectoryExists(assetsDir))
                        ctx.CopyDirectory(assetsDir, Path.Combine(outDir, "assets"));

                    var iconPath = Path.Combine(projectRoot, "modicon.png");
                    if (ctx.FileExists(iconPath))
                        ctx.CopyFile(iconPath, Path.Combine(outDir, "modicon.png"));

                    var stamped = (JObject)baseJson.DeepClone();
                    stamped["version"] = version;
                    stamped.Remove("VersionMap");

                    var outModInfo = Path.Combine(outDir, "modinfo.json");
                    if (ctx.FileExists(outModInfo)) ctx.DeleteFile(outModInfo);
                    File.WriteAllText(outModInfo, stamped.ToString(Formatting.Indented));

                    var zipPath = Path.Combine(releasesRoot, $"{modId}_{version}_{label}.zip");
                    if (ctx.FileExists(zipPath)) ctx.DeleteFile(zipPath);
                    ctx.Information($"Zipping {modId} {label} → {zipPath}");
                    ctx.Zip(outDir, zipPath);
                }
            }
        }
    }

    [TaskName("Default")]
    [IsDependentOn(typeof(PackageTask))]
    public class DefaultTask : FrostingTask { }
}
