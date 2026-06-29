FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/RagApi/RagApi.csproj src/RagApi/
RUN dotnet restore src/RagApi/RagApi.csproj

COPY src/RagApi src/RagApi
RUN dotnet publish src/RagApi/RagApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
    && apt-get install -y \
        tesseract-ocr \
        tesseract-ocr-eng \
        tesseract-ocr-vie \
        libtesseract-dev \
        libleptonica-dev \
    && mkdir -p /app/x64 \
    && leptonica_lib="$(find /usr/lib -name 'liblept.so.*' | head -n 1)" \
    && tesseract_lib="$(find /usr/lib -name 'libtesseract.so.*' | head -n 1)" \
    && if [ -n "$leptonica_lib" ]; then ln -sf "$leptonica_lib" /app/x64/libleptonica-1.82.0.so; ln -sf "$leptonica_lib" /usr/lib/libleptonica-1.82.0.so; fi \
    && if [ -n "$tesseract_lib" ]; then ln -sf "$tesseract_lib" /app/x64/libtesseract50.so; ln -sf "$tesseract_lib" /usr/lib/libtesseract50.so; fi \
    && libdl_real="$(find /usr/lib /lib -name 'libdl.so.*' | head -n 1)" \
    && if [ -n "$libdl_real" ]; then ln -sf "$libdl_real" /usr/lib/libdl.so; fi \
    && ldconfig \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV Ingestion__TessdataPath=/usr/share/tesseract-ocr/5/tessdata
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "RagApi.dll"]