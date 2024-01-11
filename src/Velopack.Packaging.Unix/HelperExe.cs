﻿using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Velopack.Packaging.Unix;

public class HelperExe : HelperFile
{
    public HelperExe(ILogger logger) : base(logger)
    {
    }

    public string VelopackEntitlements => FindHelperFile("Velopack.entitlements");

    protected string AppImageTool => FindHelperFile("appimagetool-x86_64.AppImage");

    [SupportedOSPlatform("linux")]
    public void CreateLinuxAppImage(string appDir, string outputFile)
    {
        var tool = AppImageTool;
        Chmod.ChmodFileAsExecutable(tool);
        InvokeAndThrowIfNonZero(tool, new[] { appDir, outputFile }, null);
        Chmod.ChmodFileAsExecutable(outputFile);
    }

    [SupportedOSPlatform("osx")]
    public void CodeSign(string identity, string entitlements, string filePath)
    {
        if (String.IsNullOrEmpty(entitlements)) {
            Log.Info("No entitlements specified, using default: " +
                     "https://docs.microsoft.com/en-us/dotnet/core/install/macos-notarization-issues");
            entitlements = VelopackEntitlements;
        }

        if (!File.Exists(entitlements)) {
            throw new Exception("Could not find entitlements file at: " + entitlements);
        }

        var args = new List<string> {
            "-s", identity,
            "-f",
            "-v",
            "--deep",
            "--timestamp",
            "--options", "runtime",
            "--entitlements", entitlements,
            filePath
        };

        Log.Info($"Beginning codesign for package...");

        Log.Info(InvokeAndThrowIfNonZero("codesign", args, null));

        Log.Info("codesign completed successfully");
    }

    [SupportedOSPlatform("osx")]
    public void SpctlAssessCode(string filePath)
    {
        var args2 = new List<string> {
            "--assess",
            "-vvvv",
            filePath
        };

        Log.Info($"Verifying signature/notarization for code using spctl...");
        Log.Info(InvokeAndThrowIfNonZero("spctl", args2, null));
    }

    [SupportedOSPlatform("osx")]
    public void SpctlAssessInstaller(string filePath)
    {
        var args2 = new List<string> {
            "--assess",
            "-vvv",
            "-t", "install",
            filePath
        };

        Log.Info($"Verifying signature/notarization for installer package using spctl...");
        Log.Info(InvokeAndThrowIfNonZero("spctl", args2, null));
    }

    [SupportedOSPlatform("osx")]
    public void CreateInstallerPkg(string appBundlePath, string appTitle, string appId, IEnumerable<KeyValuePair<string, string>> extraContent,
        string pkgOutputPath, string signIdentity, Action<int> progress)
    {
        // https://matthew-brett.github.io/docosx/flat_packages.html

        Log.Info($"Creating installer '.pkg' for app at '{appBundlePath}'");

        if (File.Exists(pkgOutputPath)) File.Delete(pkgOutputPath);

        using var _1 = Utility.GetTempDirectory(out var tmp);
        using var _2 = Utility.GetTempDirectory(out var tmpPayload1);
        using var _3 = Utility.GetTempDirectory(out var tmpPayload2);
        using var _4 = Utility.GetTempDirectory(out var tmpScripts);
        using var _5 = Utility.GetTempDirectory(out var tmpResources);

        // copy .app to tmp folder
        var bundleName = Path.GetFileName(appBundlePath);
        var tmpBundlePath = Path.Combine(tmpPayload1, bundleName);
        Utility.CopyFiles(new DirectoryInfo(appBundlePath), new DirectoryInfo(tmpBundlePath));
        progress(10);

        // create postinstall scripts to open app after install
        // https://stackoverflow.com/questions/35619036/open-app-after-installation-from-pkg-file-in-mac
        var postinstall = Path.Combine(tmpScripts, "postinstall");
        File.WriteAllText(postinstall, $"""
#!/bin/sh
rm -rf /tmp/velopack/{appId}
sudo -u "$USER" open "$2/{bundleName}/"
exit 0
""");
        Chmod.ChmodFileAsExecutable(postinstall);
        progress(15);

        // generate non-relocatable component pkg. this will be included into a product archive
        var pkgPlistPath = Path.Combine(tmp, "tmp.plist");
        InvokeAndThrowIfNonZero("pkgbuild", new[] { "--analyze", "--root", tmpPayload1, pkgPlistPath }, null);
        InvokeAndThrowIfNonZero("plutil", new[] { "-replace", "BundleIsRelocatable", "-bool", "NO", pkgPlistPath }, null);
        progress(50);

        var pkg1Path = Path.Combine(tmpPayload2, "1.pkg");
        string[] args1 = {
            "--root", tmpPayload1,
            "--component-plist", pkgPlistPath,
            "--scripts", tmpScripts,
            "--install-location", "/Applications",
            pkg1Path,
        };

        InvokeAndThrowIfNonZero("pkgbuild", args1, null);
        progress(70);

        // create final product package that contains app component
        var distributionPath = Path.Combine(tmp, "distribution.xml");
        InvokeAndThrowIfNonZero("productbuild", new[] { "--synthesize", "--package", pkg1Path, distributionPath }, null);
        progress(80);

        // https://developer.apple.com/library/archive/documentation/DeveloperTools/Reference/DistributionDefinitionRef/Chapters/Distribution_XML_Ref.html
        var distXml = File.ReadAllLines(distributionPath).ToList();

        distXml.Insert(2, $"<title>{SecurityElement.Escape(appTitle)}</title>");

        // disable local system installation (install to home dir)
        distXml.Insert(2, "<domains enable_anywhere=\"false\" enable_currentUserHome=\"true\" enable_localSystem=\"true\" />");

        // add extra landing content (eg. license, readme)
        foreach (var kvp in extraContent) {
            if (!String.IsNullOrEmpty(kvp.Value) && File.Exists(kvp.Value)) {
                var fileName = Path.GetFileName(kvp.Value);
                File.Copy(kvp.Value, Path.Combine(tmpResources, fileName));
                distXml.Insert(2, $"<{kvp.Key} file=\"{fileName}\" />");
            }
        }

        File.WriteAllLines(distributionPath, distXml);

        List<string> args2 = new() {
            "--distribution", distributionPath,
            "--package-path", tmpPayload2,
            "--resources", tmpResources,
            pkgOutputPath
        };

        if (!String.IsNullOrEmpty(signIdentity)) {
            args2.Add("--sign");
            args2.Add(signIdentity);
        } else {
            Log.Warn("No Installer signing identity provided. The '.pkg' will not be signed.");
        }

        InvokeAndThrowIfNonZero("productbuild", args2, null);
        progress(100);

        Log.Info("Installer created successfully");
    }

    [SupportedOSPlatform("osx")]
    public void Notarize(string filePath, string keychainProfileName)
    {
        Log.Info($"Preparing to Notarize. This will upload to Apple and usually takes minutes, [underline]but could take hours.[/]");

        var args = new List<string> {
            "notarytool",
            "submit",
            "-f", "json",
            "--keychain-profile", keychainProfileName,
            "--wait",
            filePath
        };

        var ntresultjson = InvokeProcess("xcrun", args, null);
        Log.Info(ntresultjson.StdOutput);

        // try to catch any notarization errors. if we have a submission id, retrieve notary logs.
        try {
            var ntresult = JsonConvert.DeserializeObject<NotaryToolResult>(ntresultjson.StdOutput);
            if (ntresult?.status != "Accepted" || ntresultjson.ExitCode != 0) {
                if (ntresult?.id != null) {
                    var logargs = new List<string> {
                        "notarytool",
                        "log",
                        ntresult?.id,
                        "--keychain-profile", keychainProfileName,
                    };

                    var result = InvokeProcess("xcrun", logargs, null);
                    Log.Warn(result.StdOutput);
                }

                throw new Exception("Notarization failed: " + ntresultjson.StdOutput);
            }
        } catch (JsonReaderException) {
            throw new Exception("Notarization failed: " + ntresultjson.StdOutput);
        }

        Log.Info("Notarization completed successfully");
    }

    [SupportedOSPlatform("osx")]
    public void Staple(string filePath)
    {
        Log.Debug($"Stapling Notarization to '{filePath}'");
        Log.Info(InvokeAndThrowIfNonZero("xcrun", new[] { "stapler", "staple", filePath }, null));
    }

    private class NotaryToolResult
    {
        public string id { get; set; }
        public string message { get; set; }
        public string status { get; set; }
    }

    [SupportedOSPlatform("osx")]
    public void CreateDittoZip(string folder, string outputZip)
    {
        if (File.Exists(outputZip)) File.Delete(outputZip);

        var args = new List<string> {
            "-c",
            "-k",
            "--rsrc",
            "--keepParent",
            "--sequesterRsrc",
            folder,
            outputZip
        };

        Log.Debug($"Creating ditto bundle '{outputZip}'");
        Log.Debug(InvokeAndThrowIfNonZero("ditto", args, null));
    }
}