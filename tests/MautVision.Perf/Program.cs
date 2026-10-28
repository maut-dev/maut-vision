using MautVision.Perf;

// Large library generator and benchmark. See docs/Code/review-and-testing.md and perf.ps1.
//   generate --root <dir> [--projects 40] [--files 250000] [--largest 120000] [--archived 5] [--jobs 10000]
//   bench    --root <dir> [--runs 5] [--out <file.json>]
//   bench-run --root <dir> --run <n>     one measurement in this process (started by bench)
//   bench-datasets --root <dir> [--out <file.json>]   plan, build, samples, check and export of the largest project
//   bench-transfer --root <dir> [--out <file.json>]   export and import of the largest project, then deletion of the copy
try
{
    if (args.Length == 0) return Usage();
    var options = PerfOptions.Parse(args.Skip(1).ToArray());
    switch (args[0])
    {
        case "generate":
            await LibraryGenerator.RunAsync(options);
            return 0;
        case "bench":
            return await Benchmark.RunAsync(options);
        case "bench-datasets":
            return await DatasetBenchmark.RunAsync(options);
        case "bench-transfer":
            return await TransferBenchmark.RunAsync(options);
        case "bench-run":
            await Benchmark.RunOnceAsync(options);
            return 0;
        default:
            return Usage();
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: MautVision.Perf generate|bench --root <dir> [options]");
    return 2;
}
