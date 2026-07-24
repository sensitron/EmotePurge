FROM mcr.microsoft.com/dotnet/sdk:10.0

# Convenience CLIs for talking to the sibling postgres/redis containers
RUN apt-get update && apt-get install -y --no-install-recommends \
        postgresql-client \
        redis-tools \
    && rm -rf /var/lib/apt/lists/*
