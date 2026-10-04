using System.Text.Json;
using Lab.Provisioner;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var root = Directory.GetCurrentDirectory();
    if (args.FirstOrDefault() == "init") LocalConfiguration.Generate(root);
    else if (args.FirstOrDefault() == "refresh") LocalConfiguration.RefreshRuntimeConfiguration(root);
    else if (args.FirstOrDefault() == "queues") await QueueBootstrap.RunAsync(cancellation.Token);
    else if (args.FirstOrDefault() == "bootstrap")
    {
        var settings = JsonSerializer.Deserialize<ProvisioningSettings>(await File.ReadAllTextAsync(
            Path.Combine(root, ".local", "provisioner.json"), cancellation.Token))
            ?? throw new InvalidOperationException("Private provisioning settings missing.");
        await DatabaseBootstrap.RunAsync(settings, Path.Combine(root, ".local", "certificates"), cancellation.Token);
    }
    else throw new InvalidOperationException("Usage: Lab.Provisioner init|refresh|bootstrap|queues (run from repository root).");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Provisioning failed ({exception.GetType().Name}). Check local settings and dependencies.");
    return 1;
}
