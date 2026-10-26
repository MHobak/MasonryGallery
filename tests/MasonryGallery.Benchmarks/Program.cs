using BenchmarkDotNet.Running;
using MasonryGallery.Benchmarks;

// dotnet run -c Release --project tests/MasonryGallery.Benchmarks -- <command>
//   generate <dir> [files]   synthetic library: stub JPEGs in nested folders plus a set of real-size photos
//   budgets <dir>            checks the design §2 performance budgets on a generated library (exit 1 on a miss)
//   micro [BenchmarkDotNet args]   micro-benchmarks of the hot paths
return args switch
{
    ["generate", var dir, ..] => Dataset.Generate(dir, args.Length > 2 ? int.Parse(args[2]) : Dataset.DefaultFiles),
    ["budgets", var dir, ..] => await Budgets.RunAsync(dir),
    ["micro", .. var rest] => RunMicro(rest),
    _ => Usage(),
};

static int RunMicro(string[] rest)
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(rest);
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          generate <dir> [files]         create a synthetic library (default 100,000 files)
          budgets <dir>                  measure the design's performance budgets on it
          micro [BenchmarkDotNet args]   run the micro-benchmarks, e.g. micro --filter '*Xmp*'
        """);
    return 2;
}
