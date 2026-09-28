FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Valuator/Valuator.csproj Valuator/
COPY Valuator.Shared/Valuator.Shared.csproj Valuator.Shared/
RUN dotnet restore Valuator/Valuator.csproj
COPY Valuator/ Valuator/
COPY Valuator.Shared/ Valuator.Shared/
RUN dotnet publish Valuator/Valuator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Valuator.dll"]
