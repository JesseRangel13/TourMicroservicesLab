using Microsoft.AspNetCore.Components;
namespace Gateway.Web.Catalog;
public abstract class CatalogComponent : ComponentBase, IDisposable
{
    protected CancellationTokenSource Lifetime { get; } = new();
    protected bool Busy { get; private set; }
    protected string? Error { get; private set; }
    protected string? Success { get; set; }
    protected async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        Busy = true; Error = null; Success = null;
        try { await action(Lifetime.Token); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Error = "Request timed out. Reload to check whether a change completed before retrying."; }
        catch (CatalogClientException e) { Error = e.Message; }
        catch (HttpRequestException) { Error = "Catalog is unavailable. Reload before retrying a change."; }
        finally { Busy = false; }
    }
    public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); GC.SuppressFinalize(this); }
}
