FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/RagApi/RagApi.csproj src/RagApi/
RUN dotnet restore src/RagApi/RagApi.csproj

COPY src/RagApi src/RagApi
RUN dotnet publish src/RagApi/RagApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update && apt-get install -y tesseract-ocr tesseract-ocr-vie && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV Ingestion__TessdataPath=/usr/share/tesseract-ocr/5/tessdata
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "RagApi.dll"]
