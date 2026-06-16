FROM mcr.microsoft.com/mssql/server:2025-latest

USER root
# Install the Full-Text Search package
RUN apt-get update && \
    apt-get install -y curl apt-transport-https gnupg && \
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | apt-key add - && \
    curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/mssql-server-2025.list -o /etc/apt/sources.list.d/mssql-server.list && \
    apt-get update && \
    ACCEPT_EULA=Y apt-get install -y mssql-server-fts && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

USER mssql
