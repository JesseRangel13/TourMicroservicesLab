FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:0eeb52c76e35a5431ca707ad2bc75e38006a05393045d8532ae44c15d9474523 AS build
ARG PROJECT
WORKDIR /source
COPY . .
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj --locked-mode
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble@sha256:bab27d29d13f9e903442c5fe83d7e162013747c6a101850f21049d17248afb85
ARG PROJECT
WORKDIR /app
COPY --from=build /publish .
ENV LAB_CONFIG=/run/lab/config.json SSL_CERT_FILE=/run/lab/ca.crt
ENV LAB_ASSEMBLY=${PROJECT}.dll
EXPOSE 8443
USER app
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s CMD dotnet "$LAB_ASSEMBLY" --health
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$LAB_ASSEMBLY\""]
