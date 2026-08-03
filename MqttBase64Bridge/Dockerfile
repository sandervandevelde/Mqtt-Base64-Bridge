FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MqttBase64Bridge.csproj ./
RUN dotnet restore ./MqttBase64Bridge.csproj
COPY Program.cs ./
RUN dotnet publish ./MqttBase64Bridge.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "MqttBase64Bridge.dll"]
