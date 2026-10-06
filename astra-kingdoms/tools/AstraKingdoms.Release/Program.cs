namespace AstraKingdoms.Release;

/// <summary>
/// Astra Kingdoms V1 release tooling. Every gate command prints its findings, optionally writes an
/// <c>AK-GATE-RESULT/1</c> file (<c>--out</c>) and exits 0 (pass/warn), 1 (fail), 3 (incomplete) or 2
/// (usage error). See astra-kingdoms/release/README.md for the full release flow.
/// </summary>
public static class Program
{
    public const string Usage = """
        AstraKingdoms.Release <command> [options]

          ledger-validate   --ledger <AK-ASSET-LEDGER/1>... [--unity <unity project>] [--require-approved]
                            [--forge-assets-out f --forge-provenance-out f [--include-placeholders]] [--out f]
          store-text-lint   --rules <store-lint-rules.json> --file <listing.md>... [--supported-language L]... [--out f]
          perf-compare      --report <autoplay-report.json> [--meminfo <dumpsys.txt>]... [--meminfo-dir d] [--pss-kb N] [--scenario ID]
                            [--baseline <baselines.json>] [--devices <devices.json>] [--video-capture]
                            [--update-baseline --approved-by NAME [--commit SHA]] [--out f]
          size-check        [--aab f] [--apk f] [--bundletool-size <get-size.csv> | --download-bytes N]
                            [--device-spec NAME] [--installed-bytes N] [--on-demand-bytes N] [--build-report f] [--out f]
          repro-compare     --a <build A> --b <build B> [--ignore-signing] [--allowlist <repro-allowlist.json>] [--out f]
          sustained-collate --samples <dir> --report <autoplay-report.json> [--cold-report f] [--logcat f]
                            [--min-seconds N] [--out f]
          version-policy-check --policy <minimum-version-policy.json> [--previous <old policy>] [--out f]
          closed-test-check --tracker <tester-tracker.csv> [--out f]
          data-safety       --data-map <privacy-data-map.json> --sdk-inventory <sdk-inventory.json>
                            [--candidate ID] [--md-out f] [--out f]
          release-record    --out <record.json> [--commit SHA] [--build-manifest f] [--ledger f]... [--unity dir]
                            [--evidence <AK-GATE-RESULT/1>]... [--test-results <trx|junit>]... [--declarations f]
                            [--known-issues f] [--scope T] [--support-contact T] [--rollback T] [--restore-drill T]
                            [--migration-notes T] [--purchase-ad-result T]... [--provenance-exception T]...
                            [--store-release] [--md-out f] [--forge-dir dir]

        Exit codes: 0 pass/warn, 1 fail, 2 usage error, 3 incomplete evidence (never a pass).
        """;

    private static readonly Dictionary<string, (Func<CliArgs, int> Run, string[] Flags)> Commands = new(StringComparer.Ordinal)
    {
        ["ledger-validate"] = (LedgerValidate.Cli, new[] { "require-approved", "include-placeholders" }),
        ["store-text-lint"] = (StoreTextLint.Cli, Array.Empty<string>()),
        ["perf-compare"] = (PerfCompare.Cli, new[] { "update-baseline", "video-capture" }),
        ["size-check"] = (SizeCheck.Cli, Array.Empty<string>()),
        ["repro-compare"] = (ReproCompare.Cli, new[] { "ignore-signing" }),
        ["sustained-collate"] = (SustainedCollate.Cli, Array.Empty<string>()),
        ["data-safety"] = (DataSafety.Cli, Array.Empty<string>()),
        ["closed-test-check"] = (ClosedTestCheck.Cli, Array.Empty<string>()),
        ["version-policy-check"] = (VersionPolicyCheck.Cli, Array.Empty<string>()),
        ["release-record"] = (ReleaseRecord.Cli, new[] { "store-release" }),
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" || !Commands.TryGetValue(args[0], out var cmd))
        {
            Console.Error.WriteLine(Usage);
            return args.Length == 0 || args[0] is "-h" or "--help" or "help" ? 0 : 2;
        }
        try
        {
            return cmd.Run(new CliArgs(args.Skip(1), cmd.Flags));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or FileNotFoundException or DirectoryNotFoundException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }
}
