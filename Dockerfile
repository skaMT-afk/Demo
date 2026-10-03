FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TaskFlow.Api/TaskFlow.Api.csproj TaskFlow.Api/
RUN dotnet restore TaskFlow.Api/TaskFlow.Api.csproj
COPY src/TaskFlow.Api/ TaskFlow.Api/
RUN dotnet publish TaskFlow.Api/TaskFlow.Api.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaskFlow.Api.dll"]
