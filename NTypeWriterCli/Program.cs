using System;
using System.Threading.Tasks;
using CommandLine;

namespace NTypeWriterCli
{
    class Program
    {
        class Options
        {
            [Value(0, HelpText = "Target file", Required = true)]
            public string TargetPath { get; set; }

            [Option('s', "solution", HelpText = "Target is a solution", Default = false)]
            public bool Solution { get; set; }

            [Option('p', "project", HelpText = "Target is a project", Default = false)]
            public bool Project { get; set; }

            [Option('v', "verbose", HelpText = "Print more stuff!", Default = false)]
            public bool Verbose { get; set; }
        }

        static async Task Main(string[] args)
        {
            await Parser.Default.ParseArguments<Options>(args)
                .WithParsedAsync(async o =>
                {
                    if (o.Project == o.Solution)
                    {
                        Console.Error.WriteLine("[X] You have to specify whether the target is a project or a solution!");
                        Environment.Exit(2);
                    }

                    var generator = new Generator
                    {
                        Verbose = o.Verbose
                    };

                    if (o.Project)
                        generator.LoadProject(o.TargetPath);
                    else
                        generator.LoadSolution(o.TargetPath);

                    await generator.Generate();
                });;
        }
    }
}
