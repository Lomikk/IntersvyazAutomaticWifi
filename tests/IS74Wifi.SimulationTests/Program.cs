using System.Text.Json;

namespace IS74Wifi.SimulationTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.Ordinal))
                return await SelfTestAsync();
            if (args.Contains("--help", StringComparer.Ordinal))
            {
                Console.WriteLine("IS74Wifi simulation: --days 30 --runs 1 --seed 74 --paths 1|2 --json <path> --strict");
                return 0;
            }

            var options = SimulationOptions.Parse(args);
            var results = new List<SimulationReport>();
            for (var index = 0; index < options.Runs; index++)
            {
                var seed = unchecked(options.Seed + index * 7919);
                using var simulation = new VirtualMonthSimulation(options, seed);
                var report = await simulation.RunAsync();
                results.Add(report);
                Console.WriteLine(
                    $"seed={seed} days={options.Days} paths={options.Paths} " +
                    $"ticks={report.Ticks} probes={report.Probes} stepOne={report.StepOneSends} " +
                    $"renewals={report.Renewals} outages={report.Outages} idleTicks={report.IdleTicks} " +
                    $"maxOutageSeconds={report.MaxOutageSeconds:F1} findings={report.Findings.Count}");
                foreach (var finding in report.Findings.Take(8))
                    Console.WriteLine($"  {finding.Severity}: {finding.Code} at {finding.At:O}: {finding.Detail}");
            }

            if (options.JsonPath is not null)
            {
                var fullPath = Path.GetFullPath(options.JsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(results,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Report: {fullPath}");
            }

            // Performance warnings and unavoidable silent-Windows-event outages are
            // reported, not CI failures. Only breached correctness invariants fail.
            var errors = results.Sum(result => result.Findings.Count(x => x.Severity == "error"));
            Console.WriteLine($"Virtual simulation: {results.Count} reproducible run(s), {errors} invariant failure(s).");
            return !options.Strict || errors == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Simulation failed: {ex}");
            return 2;
        }
    }

    private static async Task<int> SelfTestAsync()
    {
        var parsed = SimulationOptions.Parse(["--days", "30", "--runs", "2", "--seed", "-7", "--paths", "2", "--strict"]);
        if (parsed.Days != 30 || parsed.Runs != 2 || parsed.Seed != -7 || parsed.Paths != 2 || !parsed.Strict)
            throw new InvalidOperationException("CLI options did not round-trip.");
        try
        {
            _ = SimulationOptions.Parse(["--days", "0"]);
            throw new InvalidOperationException("An invalid simulation duration was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected validation failure.
        }
        var options = new SimulationOptions(7, 1, 417, 2, true, null);
        using var first = new VirtualMonthSimulation(options, options.Seed);
        using var second = new VirtualMonthSimulation(options, options.Seed);
        var a = await first.RunAsync();
        var b = await second.RunAsync();
        if (JsonSerializer.Serialize(a) != JsonSerializer.Serialize(b) ||
            a.Ticks == 0 || a.Probes == 0 || a.Renewals == 0 ||
            a.Findings.Any(finding => finding.Severity == "error"))
        {
            Console.Error.WriteLine("FAIL virtual simulation repeatability or scheduler invariant");
            return 1;
        }
        Console.WriteLine($"PASS virtual simulation repeatability; days=7 paths=2 ticks={a.Ticks} renewals={a.Renewals}");
        return 0;
    }
}

internal sealed record SimulationOptions(int Days, int Runs, int Seed, int Paths, bool Strict, string? JsonPath)
{
    public static SimulationOptions Parse(string[] args)
    {
        var days = 30;
        var runs = 1;
        var seed = 74;
        var paths = 1;
        var strict = false;
        string? jsonPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--strict")
            {
                strict = true;
                continue;
            }
            if (option is not ("--days" or "--runs" or "--seed" or "--paths" or "--json") || i + 1 >= args.Length)
                throw new ArgumentException($"Unknown or incomplete option: {option}");
            var value = args[++i];
            if (option == "--json")
            {
                jsonPath = value;
                continue;
            }
            if (!int.TryParse(value, out var number))
                throw new ArgumentException($"Invalid integer for {option}: {value}");
            switch (option)
            {
                case "--days": days = number; break;
                case "--runs": runs = number; break;
                case "--seed": seed = number; break;
                case "--paths": paths = number; break;
            }
        }
        if (days is < 1 or > 36500 || runs is < 1 or > 10000 || paths is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(args), "days: 1..36500, runs: 1..10000, paths: 1..2");
        return new SimulationOptions(days, runs, seed, paths, strict, jsonPath);
    }
}
