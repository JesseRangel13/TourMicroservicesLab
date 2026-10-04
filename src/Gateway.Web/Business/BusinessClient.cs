using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gateway.Web.Catalog;
using Gateway.Web.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Shared.Contracts;
namespace Gateway.Web.Business;
public sealed class BusinessClient(IHttpClientFactory factory,AuthenticationStateProvider authentication,JwtIssuer issuer,IOptions<GatewayApiOptions> options)
{
    public Task<PaymentView[]> PaymentsAsync(int page,Guid? reservation,CancellationToken ct)=>SendAsync<PaymentView[]>(HttpMethod.Get,$"payments/v1/payments?page={page}&reservationId={reservation}",null,ct);
    public Task<PaymentView> PaymentAsync(Guid id,CancellationToken ct)=>SendAsync<PaymentView>(HttpMethod.Get,$"payments/v1/payments/{id}",null,ct);
    public Task<PaymentView> ReconcileAsync(Guid id,CancellationToken ct)=>SendAsync<PaymentView>(HttpMethod.Post,$"payments/v1/admin/payments/{id}/reconcile",null,ct);
    public Task<ProviderResolution> ResolveAsync(Guid id,string outcome,CancellationToken ct)=>SendAsync<ProviderResolution>(HttpMethod.Post,$"payments/v1/admin/fake-provider/{id}/resolve",new ProviderResolution(outcome),ct);
    public Task<SagaView[]> SagasAsync(string? state,int page,CancellationToken ct)=>SendAsync<SagaView[]>(HttpMethod.Get,$"reservations/v1/admin/sagas?page={page}{(string.IsNullOrEmpty(state)?"":"&state="+Uri.EscapeDataString(state))}",null,ct);
    public Task<SagaView> RetryCompensationAsync(Guid id,CancellationToken ct)=>SendAsync<SagaView>(HttpMethod.Post,$"reservations/v1/admin/sagas/{id}/retry-compensation",null,ct);
    public Task<FaultView> CatalogFaultAsync(CancellationToken ct)=>SendAsync<FaultView>(HttpMethod.Get,"catalog/v1/admin/faults",null,ct);
    public Task<FaultView> SetCatalogFaultAsync(FaultSelection fault,CancellationToken ct)=>SendAsync<FaultView>(HttpMethod.Put,"catalog/v1/admin/faults",fault,ct);
    public Task<NotificationPage> NotificationsAsync(int page,Guid? reservation,CancellationToken ct)=>SendAsync<NotificationPage>(HttpMethod.Get,$"notifications/v1/notifications?page={page}&reservationId={reservation}",null,ct);
    public Task<NotificationView> NotificationAsync(Guid id,CancellationToken ct)=>SendAsync<NotificationView>(HttpMethod.Get,$"notifications/v1/notifications/{id}",null,ct);
    public Task<FaultView> FaultAsync(bool payments,CancellationToken ct)=>SendAsync<FaultView>(HttpMethod.Get,$"{(payments?"payments":"notifications")}/v1/admin/faults",null,ct);
    public Task<FaultView> SetFaultAsync(bool payments,FaultSelection fault,CancellationToken ct)=>SendAsync<FaultView>(HttpMethod.Put,$"{(payments?"payments":"notifications")}/v1/admin/faults",fault,ct);
    public Task<FaultView> ResetFaultAsync(bool payments,CancellationToken ct)=>SendAsync<FaultView>(HttpMethod.Post,$"{(payments?"payments":"notifications")}/v1/admin/faults/reset",null,ct);
    private async Task<T> SendAsync<T>(HttpMethod method,string path,object? body,CancellationToken ct)
    {
        var user=(await authentication.GetAuthenticationStateAsync()).User;
        var id=user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(user.Identity?.IsAuthenticated!=true || string.IsNullOrWhiteSpace(id) || !long.TryParse(user.FindFirstValue("sessionExpires"),out var expires)
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=expires)throw new CatalogClientException("Your session has expired. Sign in again.");
        using var client=factory.CreateClient("GatewayApi");using var request=new HttpRequestMessage(method,new Uri(new Uri(options.Value.BaseAddress),"api/"+path));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",issuer.Issue(id,user.Identity.Name??"",user.FindAll(ClaimTypes.Role).Select(c=>c.Value)));
        if(body is not null)request.Content=JsonContent.Create(body,body.GetType());
        using var response=await client.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)
        {
            var code="RequestFailed";
            if(response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
            {var problem=await response.Content.ReadFromJsonAsync<JsonElement>(ct);if(problem.TryGetProperty("code",out var value))code=value.GetString()??code;}
            throw new CatalogClientException($"{code} (HTTP {(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct)??throw new CatalogClientException("Empty response.");
    }
}
