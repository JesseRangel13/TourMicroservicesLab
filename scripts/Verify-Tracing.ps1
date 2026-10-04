param([ValidateSet('Gateway.Web','Catalog.Api','Reservations.Api','Payments.Api','Notifications.Api')][string]$Service='Reservations.Api')
$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$services=Invoke-RestMethod 'http://localhost:16686/api/services'
$result=Invoke-RestMethod "http://localhost:16686/api/traces?service=$([Uri]::EscapeDataString($Service))&limit=10"
$safe=@($result.data | ForEach-Object {
    $entry=$_
    [pscustomobject]@{
        TraceId=$entry.traceID
        Services=@($entry.processes.psobject.Properties.Value.serviceName | Sort-Object -Unique)
        Spans=@($entry.spans | ForEach-Object {
            [pscustomobject]@{Name=$_.operationName;SpanId=$_.spanID;Parents=@($_.references.spanID);
                Identifiers=@($_.tags | Where-Object {$_.key -in @('MessageId','SagaId','ReservationId','OperationId')} | Select-Object key,value)}
        })
    }
})
if($safe.Count -eq 0){throw 'No traces found. Enable the optional profile, run a workflow, and allow ten seconds for batching.'}
$safe | ConvertTo-Json -Depth 10 | Set-Content '.local/lab006-jaeger-traces.json'
Write-Host "Received $($safe.Count) traces, $(($safe.Spans | Measure-Object).Count) spans. Available services: $($services.data -join ', ')."
Write-Host 'Redacted evidence saved to .local/lab006-jaeger-traces.json. This read-only check does not deploy or change resources.'
