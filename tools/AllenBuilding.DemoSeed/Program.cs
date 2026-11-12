namespace AllenBuilding.DemoSeed;

// A local demonstration seed, never a web route, a production command or part of the web app.
public static class SeedCommand
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/AllenBuilding.DemoSeed -- artifacts/demo/<name>");
            return 2;
        }
        SeedReport report;
        try
        { report = await DemoSeeder.RunAsync(args[0]); }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        foreach (var check in report.Checks)
            Console.WriteLine($"{(check.Ok ? "ok  " : "FAIL")} {check.Name}: {check.Actual}" +
                (check.Ok ? "" : $" (expected {(check.Minimum ? "at least " : "")}{check.Expected})"));
        if (report.Failures.Count > 0)
        {
            Console.Error.WriteLine($"{report.Failures.Count} count(s) differ from the demo specification. Do not record from this database.");
            return 1;
        }
        Console.WriteLine($"Demo data written to {report.Directory}. Sign-in details are in accounts.json there.");
        return 0;
    }
}
