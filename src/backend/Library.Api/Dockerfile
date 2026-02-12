# Étape de build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["backend/Library.Api/Library.Api.csproj", "backend/Library.Api/"]
RUN dotnet restore "backend/Library.Api/Library.Api.csproj"

COPY . .
WORKDIR "/src/backend/Library.Api"
RUN dotnet build "Library.Api.csproj" -c Release -o /app/build

# Étape de publication
FROM build AS publish
RUN dotnet publish "Library.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Étape finale
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Création du dossier pour les livres à l'intérieur du conteneur
RUN mkdir -p /app/data_books

ENTRYPOINT ["dotnet", "Library.Api.dll"]