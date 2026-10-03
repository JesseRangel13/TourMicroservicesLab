namespace Shared.Contracts;

// Transport-only DTO; no shared domain entities or persistence models.
public sealed record ServiceStatus(string Service, string Stage, string UserId);
