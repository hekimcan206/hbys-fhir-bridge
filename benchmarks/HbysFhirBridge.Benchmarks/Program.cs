using System.Globalization;
using BenchmarkDotNet.Running;
using HbysFhirBridge.Benchmarks;
using HbysFhirBridge.Validation;

// micro:   dotnet run -c Release -- micro [BenchmarkDotNet args]
// capture: HFB_OWNER=... HFB_BRIDGE=... dotnet run -c Release -- capture --patients 300 --writers 4 --seconds 30 --max-hold-ms 50 --runs 3
// e2e:     HFB_OWNER=... HFB_BRIDGE=... HFB_FHIR=http://127.0.0.1:18480/fhir/ dotnet run -c Release -- e2e --patients 1000 --dirty-permille 20 --hold true
// Invariant culture: the tables are copied into docs verbatim, so "0.8 s" must not print as "0,8 s" on a tr-TR host.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var mode = args.FirstOrDefault() ?? "micro";
if (mode == "micro")
{
    BenchmarkRunner.Run<MicroBenchmarks>(args: args.Skip(1).ToArray());
    return;
}

var owner = Environment.GetEnvironmentVariable("HFB_OWNER") ?? throw new InvalidOperationException("HFB_OWNER is not set");
var bridge = Environment.GetEnvironmentVariable("HFB_BRIDGE") ?? throw new InvalidOperationException("HFB_BRIDGE is not set");
var validator = new ResourceValidator();
validator.WarmUp();
var experiments = new Experiments(owner, bridge, validator);

switch (mode)
{
    case "capture":
        await experiments.CaptureAsync(
            Experiments.IntArg(args, "--patients", 300),
            Experiments.IntArg(args, "--writers", 4),
            TimeSpan.FromSeconds(Experiments.IntArg(args, "--seconds", 30)),
            TimeSpan.FromMilliseconds(Experiments.IntArg(args, "--max-hold-ms", 0)),
            TimeSpan.FromMilliseconds(Experiments.IntArg(args, "--poll-ms", 200)),
            Experiments.IntArg(args, "--runs", 3));
        break;
    case "e2e":
        await experiments.EndToEndAsync(
            new Uri(Environment.GetEnvironmentVariable("HFB_FHIR") ?? "http://127.0.0.1:18480/fhir/"),
            Experiments.IntArg(args, "--patients", 1000),
            Experiments.IntArg(args, "--dirty-permille", 20) / 1000.0,
            Experiments.Arg(args, "--hold", "true") == "true",
            Experiments.IntArg(args, "--live-seconds", 60),
            Experiments.IntArg(args, "--ops-per-second", 20));
        break;
    default:
        Console.Error.WriteLine("modes: micro | capture | e2e");
        Environment.ExitCode = 2;
        break;
}
