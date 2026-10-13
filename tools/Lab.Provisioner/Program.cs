using System.Text.Json;
using Lab.Provisioner;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var root = Directory.GetCurrentDirectory();
    if (args.FirstOrDefault() == "init") LocalConfiguration.Generate(root);
    else if (args.FirstOrDefault() == "init-cloud") LocalConfiguration.Generate(root, Path.Combine(root, ".local", "aws"));
    else if (args.FirstOrDefault() == "validate-external")
    {
        if (args.Length != 2) throw new InvalidOperationException("Private settings path required.");
        var settings = JsonSerializer.Deserialize<ProvisioningSettings>(await File.ReadAllTextAsync(args[1], cancellation.Token))
            ?? throw new InvalidOperationException("Private settings missing.");
        ExternalProvisioning.Validate(settings);
        Console.WriteLine("External settings validated without a database connection.");
    }
    else if (args.FirstOrDefault() == "bootstrap-external")
    {
        if (args.Length != 3 || args[2] != "--execute-external") throw new InvalidOperationException("Explicit external migration execution required.");
        var settings = JsonSerializer.Deserialize<ProvisioningSettings>(await File.ReadAllTextAsync(args[1], cancellation.Token))
            ?? throw new InvalidOperationException("Private external provisioning settings missing.");
        ExternalProvisioning.Validate(settings);
        await DatabaseBootstrap.RunAsync(settings, Path.GetDirectoryName(Path.GetFullPath(args[1]))!, cancellation.Token);
    }
    else if (args.FirstOrDefault() == "refresh") LocalConfiguration.RefreshRuntimeConfiguration(root);
    else if (args.FirstOrDefault() == "queues") await QueueBootstrap.RunAsync(cancellation.Token);
    else if (args.FirstOrDefault() == "extensions") await ExtensionBootstrap.RunAsync(cancellation.Token);
    else if (args.FirstOrDefault() == "bootstrap")
    {
        var settings = JsonSerializer.Deserialize<ProvisioningSettings>(await File.ReadAllTextAsync(
            Path.Combine(root, ".local", "provisioner.json"), cancellation.Token))
            ?? throw new InvalidOperationException("Private provisioning settings missing.");
        await DatabaseBootstrap.RunAsync(settings, Path.Combine(root, ".local", "certificates"), cancellation.Token);
    }
    else throw new InvalidOperationException("Usage: Lab.Provisioner init|init-cloud|refresh|bootstrap|queues|extensions|validate-external <private-file>|bootstrap-external <private-file> --execute-external (run from repository root).");
    return 0;
}
catch (Exception exception)
{
    var postgres = exception as Npgsql.PostgresException ?? exception.InnerException as Npgsql.PostgresException;
    if (postgres is not null) Console.Error.WriteLine($"PostgreSQL failure SQLSTATE {postgres.SqlState}; no statement/parameters logged.");
    if (exception is InvalidOperationException && exception.Message.Contains("PendingModelChangesWarning", StringComparison.Ordinal))
        Console.Error.WriteLine("Migration models differ from snapshots; generate/rebuild migrations before bootstrap.");
    Console.Error.WriteLine($"Provisioning failed ({exception.GetType().Name}). Check local settings and dependencies.");
    return 1;
}
