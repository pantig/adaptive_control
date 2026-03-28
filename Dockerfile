FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY adaptive_control.sln ./
COPY EnergyOptimizer/EnergyOptimizer.csproj EnergyOptimizer/
RUN dotnet restore adaptive_control.sln

COPY EnergyOptimizer/ EnergyOptimizer/
RUN dotnet publish EnergyOptimizer/EnergyOptimizer.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "EnergyOptimizer.dll"]
