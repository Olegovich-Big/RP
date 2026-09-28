FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY RankCalculator/RankCalculator.csproj RankCalculator/
COPY Valuator.Shared/Valuator.Shared.csproj Valuator.Shared/
RUN dotnet restore RankCalculator/RankCalculator.csproj
COPY RankCalculator/ RankCalculator/
COPY Valuator.Shared/ Valuator.Shared/
RUN dotnet publish RankCalculator/RankCalculator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "RankCalculator.dll"]
