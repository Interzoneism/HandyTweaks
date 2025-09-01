using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Frosting;
using Cake.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CakeBuild
{
    public static class Program
    {
        public static int Main(string[] args) =>
            new CakeHost().UseContext<BuildContext>().Run(args);
    }

    // Minimal model if you ever want to deserialize strongly
    public class ModInfo
    {
        [JsonProperty("ModID")] public string ModID { get; set; }
        [JsonProperty("Version")] public string Version { get; set; }
    }

    public class BuildContext : FrostingContext
    {
        // Default project if --project not supplied
        public const string DefaultProjectName = "BetterHunger";

        public string BuildConfiguration { get; }
        public string[] ProjectPaths { get; }                 // One or many csproj paths
        public (string Label, string Tfm)[] Targets { get; } =
        {
            ("VS1.20","net7.0"),
            ("VS1.21","net8.0")
        };

        // Game roots (csproj chooses the right one by TFM)
        public string VS120 { get; }
        public string VS121 { get; }
        public string VS_Fallback { get; }

        // Optional global overrides for versions (applied if a mod has no VersionMap)
        public string VS120_VersionOverride { get; }
        public string VS121_VersionOverride { get; }

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
                // Find all csprojs one level up that have a modinfo.json next to them
                var found = ctx.GetFiles("../*/**/*.csproj")
                    .Select(f => f.FullPath)
                    .Where(p => File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "modinfo.json")))
                    .ToArray();

                // Fallback to BetterHunger if nothing found
                ProjectPaths = found.Length > 0
                    ? found
                    : new[] { $"../BetterHunger/BetterHunger.csproj" };
            }

            VS120 = ctx.EnvironmentVariable("VS120");
            VS121 = ctx.EnvironmentVariable("VS121");
            VS_Fallback = ctx.EnvironmentVariable("VINTAGE_STORY");

            VS120_VersionOverride = ctx.Argument("vs120ver", (string)null) ?? ctx.EnvironmentVariable("VS120_MODVER");
            VS121_VersionOverride = ctx.Argument("vs121ver", (string)null) ?? ctx.EnvironmentVariable("VS121_MODVER");
        }
    }

    static class Versioning
    {
        // Prefer CLI/env overrides; else VersionMap; else base Version
        public static (string v120, string v121) ResolveVersions(
            JObject baseJson, string baseVersion, string cli120, string cli121)
        {
            string map120 = FromMap(baseJson, "VS1.20");
            string map121 = FromMap(baseJson, "VS1.21");

            string v120 = cli120 ?? map120 ?? baseVersion;
            string v121 = cli121 ?? map121 ?? baseVersion;
            return (v120, v121);
        }

        public static string FromMap(JObject baseJson, string key)
        {
            try { return (baseJson["VersionMap"] as JObject)?[key]?.Value<string>(); }
            catch { return null; }
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
                    var vs120 = ctx.VS120 ?? ctx.VS_Fallback ?? "";
                    var vs121 = ctx.VS121 ?? ctx.VS_Fallback ?? "";

                    ctx.Information($"Publishing {proj} → {label} ({tfm}) …");
                    ctx.DotNetPublish(proj, new DotNetPublishSettings
                    {
                        Configuration = ctx.BuildConfiguration,
                        Framework = tfm,
                        ArgumentCustomization = args => args
                            .Append($"/p:VS120=\"{vs120}\"")
                            .Append($"/p:VS121=\"{vs121}\"")
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

                // Read base modinfo for ModID + base Version + VersionMap
                var baseJson = JObject.Parse(File.ReadAllText(baseModInfoPath));
                var modId = baseJson["ModID"]?.Value<string>() ?? Path.GetFileNameWithoutExtension(proj);
                var baseVersion = baseJson["Version"]?.Value<string>() ?? "1.0.0";

                // Resolve per-target versions
                var (v120, v121) = Versioning.ResolveVersions(baseJson, baseVersion,
                    ctx.VS120_VersionOverride, ctx.VS121_VersionOverride);

                foreach (var (label, tfm) in ctx.Targets)
                {
                    var publishDir = Path.Combine(projectRoot, $"bin/{ctx.BuildConfiguration}/Mods/mod/{tfm}/publish");
                    if (!ctx.DirectoryExists(publishDir))
                        throw new DirectoryNotFoundException($"Publish dir not found: {publishDir}");

                    var releasesRoot = "../Releases";
                    var outDir = Path.Combine(releasesRoot, $"{modId}-{label}");
                    ctx.CleanDirectory(outDir);
                    ctx.EnsureDirectoryExists(outDir);

                    // 1) Copy published binaries
                    ctx.CopyDirectory(publishDir, outDir);

                    // 2) Copy assets (if any)
                    var assetsDir = Path.Combine(projectRoot, "assets");
                    if (ctx.DirectoryExists(assetsDir))
                        ctx.CopyDirectory(assetsDir, Path.Combine(outDir, "assets"));

                    // 3) Copy modicon (if any)
                    var iconPath = Path.Combine(projectRoot, "modicon.png");
                    if (ctx.FileExists(iconPath))
                        ctx.CopyFile(iconPath, Path.Combine(outDir, "modicon.png"));

                    // 4) Stamp per-target modinfo.json (remove VersionMap)
                    var stamped = (JObject)baseJson.DeepClone();
                    var versionForThis = (label == "VS1.20") ? v120 : v121;
                    stamped["Version"] = versionForThis;
                    stamped.Remove("VersionMap");

                    var outModInfo = Path.Combine(outDir, "modinfo.json");
                    if (ctx.FileExists(outModInfo)) ctx.DeleteFile(outModInfo);
                    File.WriteAllText(outModInfo, stamped.ToString(Formatting.Indented));

                    // 5) Zip
                    var zipPath = Path.Combine(releasesRoot, $"{modId}_{versionForThis}_{label}.zip");
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
