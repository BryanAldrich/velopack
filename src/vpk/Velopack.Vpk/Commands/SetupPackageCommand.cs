﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Velopack.Vpk.Commands;
public class SetupPackageCommand : BaseCommand
{
    public string NugetPackagePath { get; set; }
    public string OutputPath { get; set; }
    public string Icon { get; set; }
    public string SignParameters { get; set; }
    public int SignParallel { get; set; }
    public string SignTemplate { get; set; }

    public SetupPackageCommand()
        : base("setup", "Create a setup package from a full nuget package.")
    {
        AddOption<FileInfo>((v) => NugetPackagePath = v.ToFullNameOrNull(), "--input", "-i")
            .SetDescription("The input file path for the nuget package.")
//            .SetArgumentHelpName("PATH")
            .SetRequired();

        AddOption<DirectoryInfo>((v) => OutputPath = v.ToFullNameOrNull(), "--output", "-o")
            .SetDescription("The output file path for the created setup package.")
//            .SetArgumentHelpName("PATH")
            .SetRequired();

        var signTemplate = AddOption<string>((v) => SignTemplate = v, "--signTemplate")
          .SetDescription("Use a custom signing command. {{file}} will be substituted.")
          .SetArgumentHelpName("COMMAND");

        AddOption<int>((v) => SignParallel = v, "--signParallel")
             .SetDescription("The number of files to sign in each signing command.")
             .SetArgumentHelpName("NUM")
             .MustBeBetween(1, 1000)
             .SetHidden()
             .SetDefault(10);

        if (VelopackRuntimeInfo.IsWindows) {
            var signParams = AddOption<string>((v) => SignParameters = v, "--signParams", "-n")
                .SetDescription("Sign files via signtool.exe using these parameters.")
                .SetArgumentHelpName("PARAMS");

            this.AreMutuallyExclusive(signTemplate, signParams);
        }
    }
}