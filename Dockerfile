FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BudgetTracker.sln ./
COPY src/BudgetTracker.Api/BudgetTracker.Api.csproj src/BudgetTracker.Api/
COPY tests/BudgetTracker.Api.Tests/BudgetTracker.Api.Tests.csproj tests/BudgetTracker.Api.Tests/
RUN dotnet restore
COPY src/ src/
RUN dotnet publish src/BudgetTracker.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BudgetTracker.Api.dll"]
